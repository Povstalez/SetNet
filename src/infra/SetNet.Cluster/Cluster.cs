using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Cluster
{
    /// <summary>Reserved wire type for cluster (server-to-server) traffic. Don't reuse it for application messages.</summary>
    public static class ClusterTypes
    {
        /// <summary>A published cluster message: <c>[nodeId][topic][body]</c>.</summary>
        public const ushort ClusterMessage = ushort.MaxValue - 34;   // 65501
    }

    /// <summary>Configuration for a single cluster node.</summary>
    public sealed class ClusterNodeOptions
    {
        /// <summary>This node's stable identity, sent with every published message (e.g. "game-eu-1").</summary>
        public string NodeId { get; set; } = "node";

        /// <summary>The TCP port this node listens on for other nodes to connect.</summary>
        public int ListenPort { get; set; }

        /// <summary>Host this node binds its listener to (default all interfaces).</summary>
        public string ListenHost { get; set; } = "0.0.0.0";

        /// <summary>The other nodes to dial and keep connected (host, port). The mesh is symmetric; seeds may overlap.</summary>
        public IReadOnlyList<(string Host, int Port)> Seeds { get; set; } = Array.Empty<(string, int)>();

        /// <summary>Delay between reconnect attempts to a seed, in milliseconds.</summary>
        public int ReconnectDelayMs { get; set; } = 2000;
    }

    /// <summary>
    /// One node in a SetNet server-to-server cluster. It runs its own dedicated listener and dials the configured seed
    /// nodes, forming a mesh that carries **only** cluster traffic (separate from your game/app server). Use it to fan
    /// out cross-node events — e.g. a global chat line, a "player X went online" notice, or an invalidation — to every
    /// other node with <see cref="Publish{T}"/>, and react with <see cref="Received"/> / <see cref="On{T}"/>.
    /// This is a lightweight broadcast bus, not a consensus/replication system.
    /// </summary>
    public sealed class ClusterNode
    {
        private readonly ClusterNodeOptions _options;
        private readonly ClusterServer _server;
        private readonly List<ClusterLink> _outbound = new List<ClusterLink>();
        private readonly ConcurrentDictionary<ClusterPeer, byte> _inbound = new ConcurrentDictionary<ClusterPeer, byte>();
        private readonly ConcurrentDictionary<string, Action<string, byte[]>> _typed = new ConcurrentDictionary<string, Action<string, byte[]>>();

        /// <summary>Raised for every message received from another node: (fromNodeId, topic, body bytes).</summary>
        public event Action<string, string, byte[]>? Received;

        /// <summary>This node's identity.</summary>
        public string NodeId => _options.NodeId;

        /// <summary>Creates a cluster node from options. Nothing connects until <see cref="StartAsync"/> is called.</summary>
        public ClusterNode(ClusterNodeOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            var serverConfig = new Configuration
            {
                Host = options.ListenHost,
                Port = options.ListenPort,
                TransportType = TransportType.Tcp,
                HeartbeatEnabled = true,
            };
            _server = new ClusterServer(serverConfig, this);
        }

        private Task? _serverLoop;

        /// <summary>Starts the node's listener and begins dialing every seed (each link retries until connected).</summary>
        public async Task StartAsync()
        {
            // BaseServer.StartAsync runs the accept loop until stopped, so it must NOT be awaited here.
            _serverLoop = _server.StartAsync();
            await Task.Delay(100).ConfigureAwait(false);   // let the listener bind before dialing
            foreach (var seed in _options.Seeds)
            {
                var link = new ClusterLink(this, seed.Host, seed.Port, _options.ReconnectDelayMs);
                lock (_outbound) _outbound.Add(link);
                link.Start();
            }
        }

        /// <summary>Publishes raw bytes on a topic to all connected nodes.</summary>
        public Task Publish(string topic, byte[] body)
        {
            var frame = Encode(_options.NodeId, topic, body);
            var sends = new List<Task>();
            foreach (var peer in _inbound.Keys) sends.Add(TrySendPeer(peer, frame));
            lock (_outbound) foreach (var link in _outbound) sends.Add(link.TrySend(frame));
            return Task.WhenAll(sends);
        }

        /// <summary>Publishes a serializable message on a topic to all connected nodes.</summary>
        public Task Publish<T>(string topic, T message) => Publish(topic, SetNetSerializer.Serialize(message));

        /// <summary>Registers a strongly-typed handler for a topic; the body is deserialized to <typeparamref name="T"/>.</summary>
        public void On<T>(string topic, Action<string, T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _typed[topic] = (from, body) => handler(from, SetNetSerializer.Deserialize<T>(body));
        }

        /// <summary>Stops the listener and all outbound links.</summary>
        public async Task Stop()
        {
            await _server.StopAsync().ConfigureAwait(false);
            lock (_outbound) { foreach (var link in _outbound) link.Stop(); _outbound.Clear(); }
        }

        internal void RegisterInbound(ClusterPeer peer) => _inbound.TryAdd(peer, 0);
        internal void UnregisterInbound(ClusterPeer peer) => _inbound.TryRemove(peer, out _);

        internal void OnFrame(byte[] payload)
        {
            if (!TryDecode(payload, out var from, out var topic, out var body)) return;
            Received?.Invoke(from, topic, body);
            if (_typed.TryGetValue(topic, out var typed)) typed(from, body);
        }

        private static async Task TrySendPeer(ClusterPeer peer, byte[] frame)
        {
            try { await peer.SendRawAsync(ClusterTypes.ClusterMessage, frame, DeliveryMethod.Reliable).ConfigureAwait(false); }
            catch { /* link down; the peer will be removed on disconnect */ }
        }

        // Wire: [1 nodeIdLen][nodeId utf8][2 topicLen LE][topic utf8][body...]
        private static byte[] Encode(string nodeId, string topic, byte[] body)
        {
            var id = Encoding.UTF8.GetBytes(nodeId);
            var top = Encoding.UTF8.GetBytes(topic);
            if (id.Length > 255) throw new ArgumentException("NodeId too long (max 255 UTF-8 bytes).");
            var frame = new byte[1 + id.Length + 2 + top.Length + body.Length];
            var o = 0;
            frame[o++] = (byte)id.Length;
            Buffer.BlockCopy(id, 0, frame, o, id.Length); o += id.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(o, 2), (ushort)top.Length); o += 2;
            Buffer.BlockCopy(top, 0, frame, o, top.Length); o += top.Length;
            Buffer.BlockCopy(body, 0, frame, o, body.Length);
            return frame;
        }

        private static bool TryDecode(byte[] f, out string nodeId, out string topic, out byte[] body)
        {
            nodeId = topic = string.Empty; body = Array.Empty<byte>();
            if (f.Length < 3) return false;
            var o = 0;
            int idLen = f[o++];
            if (o + idLen + 2 > f.Length) return false;
            nodeId = Encoding.UTF8.GetString(f, o, idLen); o += idLen;
            int topLen = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(o, 2)); o += 2;
            if (o + topLen > f.Length) return false;
            topic = Encoding.UTF8.GetString(f, o, topLen); o += topLen;
            body = new byte[f.Length - o];
            Buffer.BlockCopy(f, o, body, 0, body.Length);
            return true;
        }
    }

    /// <summary>Internal listener that accepts inbound cluster links.</summary>
    internal sealed class ClusterServer : BaseServer
    {
        private readonly ClusterNode _node;
        public ClusterServer(Configuration config, ClusterNode node) : base(config) { _node = node; }
        protected override BasePeer OnNewClient(PeerInfo peerInfo) => new ClusterPeer(peerInfo, _node);
    }

    /// <summary>Internal server-side peer: routes inbound cluster frames to the node.</summary>
    internal sealed class ClusterPeer : BasePeer
    {
        private readonly ClusterNode _node;
        public ClusterPeer(PeerInfo peerInfo, ClusterNode node) : base(peerInfo) { _node = node; _node.RegisterInbound(this); }

        protected override bool OnRawFrame(ushort type, byte[] data)
        {
            if (type != ClusterTypes.ClusterMessage) return false;
            _node.OnFrame(data);
            return true;
        }

        protected override void OnDisconnected() => _node.UnregisterInbound(this);
    }

    /// <summary>
    /// Internal outbound link (a client) that dials a seed node and keeps it connected. It runs its own
    /// maintenance loop that retries the <b>initial</b> connect (a seed may be down at startup) and re-dials
    /// after any drop — core auto-reconnect only fires once a connection has succeeded, which isn't enough here.
    /// </summary>
    internal sealed class ClusterLink : BaseClient
    {
        private readonly ClusterNode _node;
        private readonly int _reconnectDelayMs;
        private volatile bool _running;

        public ClusterLink(ClusterNode node, string host, int port, int reconnectDelayMs)
            : base(new Configuration
            {
                Host = host,
                Port = port,
                TransportType = TransportType.Tcp,
                HeartbeatEnabled = true,
                AutoReconnect = false,   // we drive (re)connection ourselves so initial-connect failures also retry
            })
        {
            _node = node;
            _reconnectDelayMs = reconnectDelayMs;
        }

        public void Start() { _running = true; _ = MaintainAsync(); }

        public async Task TrySend(byte[] frame)
        {
            try { await SendRawAsync(ClusterTypes.ClusterMessage, frame, DeliveryMethod.Reliable).ConfigureAwait(false); }
            catch { /* not connected yet / dropping */ }
        }

        // Keep trying until connected; returns once connected. A later drop re-enters via OnDisconnected.
        private async Task MaintainAsync()
        {
            while (_running)
            {
                try { await ConnectAsync().ConfigureAwait(false); return; }
                catch { /* seed down; retry after a delay */ }
                await Task.Delay(_reconnectDelayMs).ConfigureAwait(false);
            }
        }

        protected override bool OnRawFrame(ushort type, byte[] data)
        {
            if (type != ClusterTypes.ClusterMessage) return false;
            _node.OnFrame(data);
            return true;
        }

        public void Stop() { _running = false; Disconnect(); }

        protected override void OnConnected() { }
        protected override void OnDisconnected() { if (_running) _ = MaintainAsync(); }   // re-establish after a drop
        protected override void OnError(string error) { }
    }

    /// <summary>Optional bootstrap for symmetry with other SetNet packages. The cluster has no auto-discovered handlers.</summary>
    public static class ClusterRuntime
    {
        /// <summary>No-op; cluster routing uses the raw-frame hook, so nothing needs discovering.</summary>
        public static void Enable() { }
    }
}
