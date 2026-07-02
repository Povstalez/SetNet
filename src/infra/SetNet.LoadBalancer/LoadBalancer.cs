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

namespace SetNet.LoadBalancer
{
    /// <summary>Reserved wire types for the load-balancer service. Don't reuse these ids for application messages.</summary>
    public static class LoadBalancerTypes
    {
        /// <summary>Client → server: pick-a-node command.</summary>
        public const ushort Command = ushort.MaxValue - 80;   // 65455

        /// <summary>Server → client: correlated reply (the chosen node).</summary>
        public const ushort Reply = ushort.MaxValue - 81;     // 65454
    }

    /// <summary>Thrown when node selection fails (no capacity, timeout).</summary>
    public sealed class LoadBalancerException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public LoadBalancerException(string message) : base(message) { }
    }

    /// <summary>A game-server node the balancer can direct clients to.</summary>
    public sealed class LbNode
    {
        /// <summary>Stable unique node id.</summary>
        public string NodeId { get; set; } = "";

        /// <summary>Public host clients connect to.</summary>
        public string Host { get; set; } = "";

        /// <summary>Public port clients connect to.</summary>
        public int Port { get; set; }

        /// <summary>Current load (e.g. connected players). Lower is preferred.</summary>
        public int Load { get; set; }

        /// <summary>Max load before the node is considered full (0 = unlimited).</summary>
        public int Capacity { get; set; }

        /// <summary>True when the node is at or over capacity.</summary>
        public bool IsFull => Capacity > 0 && Load >= Capacity;

        /// <summary>Load as a fraction of capacity (unbounded when capacity is 0, so uncapped nodes fill last-ish by raw load).</summary>
        internal double Ratio => Capacity > 0 ? (double)Load / Capacity : Load;

        /// <summary>Creates an empty node (for serialization).</summary>
        public LbNode() { }

        /// <summary>Creates a node descriptor.</summary>
        public LbNode(string nodeId, string host, int port, int load = 0, int capacity = 0)
        { NodeId = nodeId; Host = host; Port = port; Load = load; Capacity = capacity; }
    }

    // ---- wire ----

    internal static class LbCodec
    {
        public static byte[] EncodeReply(int corr, bool ok, string error, LbNode? node)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(ok); w.Write(error ?? "");
            w.Write(node != null);
            if (node != null) { w.Write(node.NodeId ?? ""); w.Write(node.Host ?? ""); w.Write(node.Port); w.Write(node.Load); w.Write(node.Capacity); }
            return ms.ToArray();
        }

        public static (int Corr, bool Ok, string Error, LbNode? Node) DecodeReply(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32(); var ok = r.ReadBoolean(); var err = r.ReadString();
            LbNode? node = null;
            if (r.ReadBoolean()) node = new LbNode(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            return (corr, ok, err, node);
        }
    }

    internal static class LbRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<(bool Ok, string Error, LbNode? Node)>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<(bool, string, LbNode?)>>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<(bool, string, LbNode?)> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, (bool, string, LbNode?) r) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(r); }
    }

    /// <summary>
    /// Client-side load-balancer driver, attached by <see cref="LoadBalancerClientExtensions.UseLoadBalancer"/>. Ask
    /// a well-known entry node for the least-loaded game node, then connect there. Pairs with <c>SetNet.Sharding</c>
    /// (which routes by key) when placement is capacity-driven rather than key-driven.
    /// </summary>
    public sealed class LoadBalancerClient
    {
        private readonly BaseClient _client;

        internal LoadBalancerClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Returns the least-loaded node with spare capacity; throws <see cref="LoadBalancerException"/> if all are full.</summary>
        public async Task<LbNode> PickAsync()
        {
            var id = LbRegistry.NextId();
            var tcs = new TaskCompletionSource<(bool, string, LbNode?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            LbRegistry.Register(id, tcs);
            try
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) w.Write(id);
                await _client.SendAsync(LoadBalancerTypes.Command, ms.ToArray(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    (bool Ok, string Error, LbNode? Node) result;
                    try { result = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new LoadBalancerException("Node selection timed out."); }
                    if (!result.Ok || result.Node == null) throw new LoadBalancerException(result.Error.Length > 0 ? result.Error : "No node available.");
                    return result.Node;
                }
            }
            finally { LbRegistry.Remove(id); }
        }
    }

    /// <summary>
    /// Server-side load-balancer directory, attached by <see cref="LoadBalancerServerExtensions.UseLoadBalancer"/>.
    /// Holds a registry of game nodes with their reported load; clients ask for the least-loaded one with spare
    /// capacity. The registry is fed by the app (from a <c>SetNet.Cluster</c> gossip, an orchestrator, or health
    /// pings) via <see cref="UpdateNode"/> / <see cref="RemoveNode"/> — this package doesn't gossip on its own.
    /// </summary>
    public sealed class LoadBalancerServer
    {
        private static readonly ConcurrentDictionary<BaseServer, LoadBalancerServer> Servers = new ConcurrentDictionary<BaseServer, LoadBalancerServer>();

        private readonly ConcurrentDictionary<string, LbNode> _nodes = new ConcurrentDictionary<string, LbNode>();

        internal static LoadBalancerServer Enable(BaseServer server) => Servers.GetOrAdd(server, _ => new LoadBalancerServer());

        internal static LoadBalancerServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Adds or updates a node's descriptor (id, address, load, capacity).</summary>
        public LoadBalancerServer UpdateNode(LbNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.NodeId)) throw new ArgumentException("Node needs a non-empty id.", nameof(node));
            _nodes[node.NodeId] = node;
            return this;
        }

        /// <summary>Updates just a known node's load (a lighter hot-path than replacing the whole descriptor).</summary>
        public void ReportLoad(string nodeId, int load)
        {
            if (_nodes.TryGetValue(nodeId ?? "", out var node)) node.Load = load;
        }

        /// <summary>Removes a node from the pool (drained/dead).</summary>
        public bool RemoveNode(string nodeId) => _nodes.TryRemove(nodeId ?? "", out _);

        /// <summary>The current node pool.</summary>
        public IReadOnlyCollection<LbNode> Nodes => new List<LbNode>(_nodes.Values);

        /// <summary>Selects the node with the lowest load ratio that still has spare capacity; null when all are full/empty.</summary>
        public LbNode? Pick()
        {
            LbNode? best = null;
            foreach (var node in _nodes.Values)
            {
                if (node.IsFull) continue;
                if (best == null || node.Ratio < best.Ratio) best = node;
            }
            return best;
        }

        internal Task OnQuery(BasePeer peer, int correlationId)
        {
            var node = Pick();
            var reply = node != null
                ? LbCodec.EncodeReply(correlationId, true, "", node)
                : LbCodec.EncodeReply(correlationId, false, "All nodes are full.", null);
            try { return peer.SendAsync(LoadBalancerTypes.Reply, reply, DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for node-selection queries.</summary>
    [MessageHandler(LoadBalancerTypes.Command)]
    public sealed class LoadBalancerCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = LoadBalancerServer.For(peer.CurrentPeerInfo.Server);
            if (hub == null) return Task.CompletedTask;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return hub.OnQuery(peer, r.ReadInt32());
        }
    }

    /// <summary>Auto-discovered client handler for correlated node-selection replies.</summary>
    [MessageHandler(LoadBalancerTypes.Reply)]
    public sealed class LoadBalancerReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, ok, err, node) = LbCodec.DecodeReply(data); LbRegistry.Complete(corr, (ok, err, node)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the load-balancer directory to a server by composition.</summary>
    public static class LoadBalancerServerExtensions
    {
        /// <summary>Enables the server-side load-balancer directory; returns it so the app can feed node loads.</summary>
        public static LoadBalancerServer UseLoadBalancer(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return LoadBalancerServer.Enable(server);
        }
    }

    /// <summary>Attaches a load-balancer client by composition.</summary>
    public static class LoadBalancerClientExtensions
    {
        /// <summary>Enables client-side node selection; returns the driver (<c>PickAsync</c>).</summary>
        public static LoadBalancerClient UseLoadBalancer(this BaseClient client) => new LoadBalancerClient(client);
    }

    /// <summary>One-time bootstrap so the load-balancer handlers are discovered. Call at startup.</summary>
    public static class LoadBalancerRuntime
    {
        /// <summary>Ensures the load-balancer layer is discoverable.</summary>
        public static void Enable() { _ = LoadBalancerTypes.Command; }
    }
}
