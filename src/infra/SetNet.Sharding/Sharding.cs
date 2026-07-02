using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Sharding
{
    /// <summary>Reserved wire types for the shard directory. Don't reuse these ids for application messages.</summary>
    public static class ShardingTypes
    {
        /// <summary>Client → server: locate/list command.</summary>
        public const ushort Command = ushort.MaxValue - 44;   // 65491

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 45;     // 65490
    }

    internal enum ShardOp : byte { Locate = 0, List = 1 }

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

    internal sealed class ShardCommand
    {
        public int CorrelationId;
        public ShardOp Op;
        public string Key = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write((byte)Op);
            w.Write(Key ?? "");
            return ms.ToArray();
        }

        public static ShardCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new ShardCommand
            {
                CorrelationId = r.ReadInt32(),
                Op = (ShardOp)r.ReadByte(),
                Key = r.ReadString(),
            };
        }
    }

    internal sealed class ShardReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public ShardNode[] Nodes = Array.Empty<ShardNode>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(Nodes.Length);
            foreach (var n in Nodes)
            {
                w.Write(n.NodeId ?? "");
                w.Write(n.Host ?? "");
                w.Write(n.Port);
            }
            return ms.ToArray();
        }

        public static ShardReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new ShardReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
            };
            var count = r.ReadInt32();
            reply.Nodes = new ShardNode[count];
            for (var i = 0; i < count; i++)
                reply.Nodes[i] = new ShardNode { NodeId = r.ReadString(), Host = r.ReadString(), Port = r.ReadInt32() };
            return reply;
        }
    }

    // ---- client-side plumbing ----

    internal static class ShardingRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<ShardReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<ShardReply>>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<ShardReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, ShardReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
    }

    /// <summary>
    /// Client-side shard directory, attached by <see cref="ShardingClientExtensions.UseSharding"/>. Ask any node
    /// which node owns a key (a room code, a world region, a player id) and connect there — the usual flow is:
    /// connect to a well-known entry node, <see cref="LocateAsync"/>, then dial the returned node for gameplay.
    /// </summary>
    public sealed class ShardingClient
    {
        private readonly BaseClient _client;

        internal ShardingClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>The node that owns <paramref name="key"/> per the server's ring.</summary>
        public async Task<ShardNode> LocateAsync(string key)
        {
            var reply = await SendCommand(ShardOp.Locate, key).ConfigureAwait(false);
            if (!reply.Success) throw new ShardingException(reply.Error);
            if (reply.Nodes.Length == 0) throw new ShardingException("Shard ring is empty.");
            return reply.Nodes[0];
        }

        /// <summary>All nodes currently on the server's ring.</summary>
        public async Task<IReadOnlyList<ShardNode>> ListNodesAsync()
        {
            var reply = await SendCommand(ShardOp.List, "").ConfigureAwait(false);
            if (!reply.Success) throw new ShardingException(reply.Error);
            return reply.Nodes;
        }

        private async Task<ShardReply> SendCommand(ShardOp op, string key)
        {
            var id = ShardingRegistry.NextId();
            var tcs = new TaskCompletionSource<ShardReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            ShardingRegistry.Register(id, tcs);
            try
            {
                var cmd = new ShardCommand { CorrelationId = id, Op = op, Key = key };
                await _client.SendAsync(ShardingTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new ShardingException("Shard directory query timed out."); }
                }
            }
            finally { ShardingRegistry.Remove(id); }
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

        internal async Task OnCommand(BasePeer peer, ShardCommand cmd)
        {
            switch (cmd.Op)
            {
                case ShardOp.Locate:
                {
                    var node = Locate(cmd.Key);
                    if (node == null) await Reply(peer, cmd.CorrelationId, false, "Shard ring is empty.", Array.Empty<ShardNode>()).ConfigureAwait(false);
                    else await Reply(peer, cmd.CorrelationId, true, "", new[] { node }).ConfigureAwait(false);
                    break;
                }
                case ShardOp.List:
                {
                    var ring = _ring;
                    var nodes = new ShardNode[ring.Nodes.Count];
                    for (var i = 0; i < nodes.Length; i++) nodes[i] = ring.Nodes[i];
                    await Reply(peer, cmd.CorrelationId, true, "", nodes).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string err, ShardNode[] nodes)
        {
            var reply = new ShardReply { CorrelationId = corr, Success = ok, Error = err, Nodes = nodes };
            return peer.SendAsync(ShardingTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for shard directory queries.</summary>
    [MessageHandler(ShardingTypes.Command)]
    public sealed class ShardCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var directory = ShardingServer.For(peer.CurrentPeerInfo.Server);
            return directory?.OnCommand(peer, ShardCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated shard directory replies.</summary>
    [MessageHandler(ShardingTypes.Reply)]
    public sealed class ShardReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = ShardReply.Decode(data); ShardingRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
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

    /// <summary>One-time bootstrap so the sharding handlers are discovered. Call at startup.</summary>
    public static class ShardingRuntime
    {
        /// <summary>Ensures the sharding layer is discoverable.</summary>
        public static void Enable() { _ = ShardingTypes.Command; }
    }
}
