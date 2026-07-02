using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Sharding
{
    /// <summary>Command operations (client → server) within the Sharding protocol channel.</summary>
    internal enum ShardOp : ushort { Locate = 1, List = 2 }

    /// <summary>Thrown when a shard directory query fails (empty ring, timeout).</summary>
    public sealed class ShardingException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public ShardingException(string message) : base(message) { }
    }

    /// <summary>One server node in the shard ring — the address clients should connect to for keys it owns.</summary>
    public sealed class ShardNode
    {
        /// <summary>Stable unique node identifier (also the ring-hash seed — changing it remaps the node's keys).</summary>
        public string NodeId { get; set; } = "";

        /// <summary>Public host clients connect to.</summary>
        public string Host { get; set; } = "";

        /// <summary>Public port clients connect to.</summary>
        public int Port { get; set; }

        /// <inheritdoc/>
        public override string ToString() => $"{NodeId}@{Host}:{Port}";
    }

    /// <summary>
    /// A consistent-hash ring with virtual nodes. Each node is hashed onto the ring <c>VirtualNodes</c> times;
    /// a key belongs to the first node clockwise from its hash. Adding or removing one node remaps only
    /// ~1/N of the keys — the property that makes rebalancing cheap. Immutable once built; swap in a new ring
    /// to change membership (see <see cref="ShardingServer.UpdateNodes"/>).
    /// </summary>
    public sealed class ShardRing
    {
        private readonly ulong[] _points;      // sorted virtual-node hashes
        private readonly ShardNode[] _owners;  // owner of each point (parallel to _points)

        /// <summary>The distinct nodes on the ring, in construction order.</summary>
        public IReadOnlyList<ShardNode> Nodes { get; }

        /// <summary>Builds the ring from <paramref name="nodes"/> with <paramref name="virtualNodes"/> points per node.</summary>
        public ShardRing(IEnumerable<ShardNode> nodes, int virtualNodes = 128)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));
            if (virtualNodes < 1) throw new ArgumentOutOfRangeException(nameof(virtualNodes));

            var list = new List<ShardNode>();
            foreach (var n in nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.NodeId)) throw new ArgumentException("Every node needs a non-empty NodeId.", nameof(nodes));
                list.Add(n);
            }
            Nodes = list;

            var points = new List<(ulong Hash, ShardNode Node)>(list.Count * virtualNodes);
            foreach (var node in list)
                for (var i = 0; i < virtualNodes; i++)
                    points.Add((Fnv1a64($"{node.NodeId}#{i}"), node));
            points.Sort((a, b) => a.Hash.CompareTo(b.Hash));

            _points = new ulong[points.Count];
            _owners = new ShardNode[points.Count];
            for (var i = 0; i < points.Count; i++) { _points[i] = points[i].Hash; _owners[i] = points[i].Node; }
        }

        /// <summary>The node that owns <paramref name="key"/>; null when the ring is empty.</summary>
        public ShardNode? GetNode(string key)
        {
            if (_points.Length == 0) return null;
            return _owners[IndexFor(Fnv1a64(key ?? ""))];
        }

        /// <summary>
        /// The first <paramref name="count"/> distinct nodes clockwise from <paramref name="key"/> — the owner
        /// followed by its natural replicas.
        /// </summary>
        public IReadOnlyList<ShardNode> GetNodes(string key, int count)
        {
            var result = new List<ShardNode>();
            if (_points.Length == 0 || count < 1) return result;

            var start = IndexFor(Fnv1a64(key ?? ""));
            for (var step = 0; step < _points.Length && result.Count < count; step++)
            {
                var owner = _owners[(start + step) % _points.Length];
                if (!result.Contains(owner)) result.Add(owner);
            }
            return result;
        }

        /// <summary>Binary-search for the first ring point at or after <paramref name="hash"/> (wrapping past the top).</summary>
        private int IndexFor(ulong hash)
        {
            var lo = 0;
            var hi = _points.Length;   // exclusive
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_points[mid] < hash) lo = mid + 1; else hi = mid;
            }
            return lo == _points.Length ? 0 : lo;   // wrap: past the last point → first point
        }

        /// <summary>FNV-1a 64-bit over the UTF-16 code units — fast, allocation-free, stable across platforms/restarts.</summary>
        internal static ulong Fnv1a64(string text)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                foreach (var ch in text)
                {
                    hash ^= (byte)ch;
                    hash *= 1099511628211UL;
                    hash ^= (byte)(ch >> 8);
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }
    }

    /// <summary>Settings for the shard directory.</summary>
    public sealed class ShardingOptions
    {
        /// <summary>The cluster's nodes (every node should be configured with the same list).</summary>
        public List<ShardNode> Nodes { get; set; } = new List<ShardNode>();

        /// <summary>This node's id within <see cref="Nodes"/>; enables <see cref="ShardingServer.IsLocal"/>. Optional.</summary>
        public string? SelfNodeId { get; set; }

        /// <summary>Ring points per node (default 128 — higher = smoother key distribution, larger ring).</summary>
        public int VirtualNodes { get; set; } = 128;
    }

    // ---- wire ----

    internal static class ShardCodec
    {
        public static byte[] EncodeQuery(string key)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(key ?? "");
            return ms.ToArray();
        }

        public static string DecodeQuery(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }

        public static byte[] EncodeNodes(IReadOnlyList<ShardNode> nodes)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(nodes.Count);
            foreach (var n in nodes)
            {
                w.Write(n.NodeId ?? "");
                w.Write(n.Host ?? "");
                w.Write(n.Port);
            }
            return ms.ToArray();
        }

        public static ShardNode[] DecodeNodes(byte[] data)
        {
            if (data == null || data.Length == 0) return Array.Empty<ShardNode>();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var count = r.ReadInt32();
            var nodes = new ShardNode[count];
            for (var i = 0; i < count; i++)
                nodes[i] = new ShardNode { NodeId = r.ReadString(), Host = r.ReadString(), Port = r.ReadInt32() };
            return nodes;
        }
    }

    // ---- client ----

    /// <summary>
    /// Client-side shard directory, attached by <see cref="ShardingClientExtensions.UseSharding"/>. Ask any node
    /// which node owns a key (a room code, a world region, a player id) and connect there — the usual flow is:
    /// connect to a well-known entry node, <see cref="LocateAsync"/>, then dial the returned node for gameplay.
    /// Rides the unified protocol on the <see cref="Channels.Sharding"/> channel.
    /// </summary>
    public sealed class ShardingClient
    {
        private readonly BaseClient _client;

        internal ShardingClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>The node that owns <paramref name="key"/> per the server's ring.</summary>
        public async Task<ShardNode> LocateAsync(string key)
        {
            var nodes = await Query(ShardOp.Locate, key).ConfigureAwait(false);
            if (nodes.Length == 0) throw new ShardingException("Shard ring is empty.");
            return nodes[0];
        }

        /// <summary>All nodes currently on the server's ring.</summary>
        public async Task<IReadOnlyList<ShardNode>> ListNodesAsync()
            => await Query(ShardOp.List, "").ConfigureAwait(false);

        private async Task<ShardNode[]> Query(ShardOp op, string key)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Sharding, (ushort)op, ShardCodec.EncodeQuery(key)).ConfigureAwait(false);
                return ShardCodec.DecodeNodes(body);
            }
            catch (ProtocolException ex) { throw new ShardingException(ex.Message); }
            catch (TimeoutException) { throw new ShardingException("Shard directory query timed out."); }
        }
    }

    // ---- server side ----

    /// <summary>
    /// Server-side shard directory, attached by <see cref="ShardingServerExtensions.UseSharding"/>. Every node runs
    /// the same directory with the same node list, so clients can ask <b>any</b> node where a key lives. Also usable
    /// in-process: <see cref="Locate"/>/<see cref="IsLocal"/> route server-side work (e.g. only create a room whose
    /// code is local, redirect otherwise). Membership changes go through <see cref="UpdateNodes"/> — broadcast them
    /// yourself (e.g. over <c>SetNet.Cluster</c>) so all nodes swap rings together.
    /// </summary>
    public sealed class ShardingServer
    {
        private static readonly ConcurrentDictionary<BaseServer, ShardingServer> Servers = new ConcurrentDictionary<BaseServer, ShardingServer>();

        private readonly int _virtualNodes;
        private readonly string? _selfNodeId;
        private volatile ShardRing _ring;

        /// <summary>The current ring (swapped atomically by <see cref="UpdateNodes"/>).</summary>
        public ShardRing Ring => _ring;

        internal ShardingServer(ShardingOptions options)
        {
            _virtualNodes = options.VirtualNodes;
            _selfNodeId = options.SelfNodeId;
            _ring = new ShardRing(options.Nodes, options.VirtualNodes);
        }

        internal static ShardingServer Enable(BaseServer server, ShardingOptions options)
            => Servers.GetOrAdd(server, _ => new ShardingServer(options));

        internal static ShardingServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Replaces the ring with a new node list (atomic swap; in-flight queries keep the old ring).</summary>
        public void UpdateNodes(IEnumerable<ShardNode> nodes) => _ring = new ShardRing(nodes, _virtualNodes);

        /// <summary>The node that owns <paramref name="key"/>; null when the ring is empty.</summary>
        public ShardNode? Locate(string key) => _ring.GetNode(key);

        /// <summary>True when this node (per <see cref="ShardingOptions.SelfNodeId"/>) owns <paramref name="key"/>.</summary>
        public bool IsLocal(string key)
        {
            if (_selfNodeId == null) return false;
            var owner = _ring.GetNode(key);
            return owner != null && owner.NodeId == _selfNodeId;
        }

        internal Task HandleAsync(ChannelRequest request)
        {
            switch ((ShardOp)request.Op)
            {
                case ShardOp.Locate:
                {
                    var node = Locate(ShardCodec.DecodeQuery(request.RawBody));
                    if (node == null) throw new ProtocolException("Shard ring is empty.");
                    return request.ReplyRawAsync(ShardCodec.EncodeNodes(new[] { node }));
                }
                case ShardOp.List:
                {
                    var ring = _ring;
                    var nodes = new ShardNode[ring.Nodes.Count];
                    for (var i = 0; i < nodes.Length; i++) nodes[i] = ring.Nodes[i];
                    return request.ReplyRawAsync(ShardCodec.EncodeNodes(nodes));
                }
                default:
                    throw new ProtocolException($"Unknown sharding op {request.Op}.");
            }
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for shard directory queries.</summary>
    [ProtocolChannel(Channels.Sharding)]
    public sealed class ShardingChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var directory = ShardingServer.For(request.Peer.CurrentPeerInfo.Server);
            if (directory == null) throw new ProtocolException("sharding is not configured on this server");
            return directory.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the shard directory to a server by composition.</summary>
    public static class ShardingServerExtensions
    {
        /// <summary>Enables the shard directory; returns it for in-process routing (<c>Locate</c>/<c>IsLocal</c>/<c>UpdateNodes</c>).</summary>
        public static ShardingServer UseSharding(this BaseServer server, ShardingOptions options)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return ShardingServer.Enable(server, options);
        }
    }

    /// <summary>Attaches a shard directory client by composition.</summary>
    public static class ShardingClientExtensions
    {
        /// <summary>Enables client-side shard queries; returns the driver (<c>LocateAsync</c>/<c>ListNodesAsync</c>).</summary>
        public static ShardingClient UseSharding(this BaseClient client) => new ShardingClient(client);
    }

    /// <summary>One-time bootstrap so the sharding channel service is discovered. Call at startup.</summary>
    public static class ShardingRuntime
    {
        /// <summary>Ensures the sharding layer is discoverable.</summary>
        public static void Enable() { _ = typeof(ShardingChannelService); }
    }
}
