using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.LoadBalancer
{
    /// <summary>Command operations (client → server) within the LoadBalancer protocol channel.</summary>
    internal enum LoadBalancerOp : ushort { Pick = 1 }

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
        public static byte[] EncodeNode(LbNode node)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(node.NodeId ?? ""); w.Write(node.Host ?? ""); w.Write(node.Port); w.Write(node.Load); w.Write(node.Capacity);
            return ms.ToArray();
        }

        public static LbNode DecodeNode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new LbNode(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        }
    }

    /// <summary>
    /// Client-side load-balancer driver, attached by <see cref="LoadBalancerClientExtensions.UseLoadBalancer"/>. Ask
    /// a well-known entry node for the least-loaded game node, then connect there. Pairs with <c>SetNet.Sharding</c>
    /// (which routes by key) when placement is capacity-driven rather than key-driven. Rides the unified protocol on
    /// the <see cref="Channels.LoadBalancer"/> channel.
    /// </summary>
    public sealed class LoadBalancerClient
    {
        private readonly BaseClient _client;

        internal LoadBalancerClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Returns the least-loaded node with spare capacity; throws <see cref="LoadBalancerException"/> if all are full.</summary>
        public async Task<LbNode> PickAsync()
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.LoadBalancer, (ushort)LoadBalancerOp.Pick, Array.Empty<byte>()).ConfigureAwait(false);
                return LbCodec.DecodeNode(body);
            }
            catch (ProtocolException ex) { throw new LoadBalancerException(ex.Message); }
            catch (TimeoutException) { throw new LoadBalancerException("Node selection timed out."); }
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

        internal Task HandleAsync(ChannelRequest request)
        {
            var node = Pick();
            if (node == null) throw new ProtocolException("All nodes are full.");
            return request.ReplyRawAsync(LbCodec.EncodeNode(node));
        }
    }

    /// <summary>Auto-discovered channel service for node-selection queries.</summary>
    [ProtocolChannel(Channels.LoadBalancer)]
    public sealed class LoadBalancerChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = LoadBalancerServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("load balancer is not configured on this server");
            return hub.HandleAsync(request);
        }
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

    /// <summary>One-time bootstrap so the load-balancer channel service is discovered. Call at startup.</summary>
    public static class LoadBalancerRuntime
    {
        /// <summary>Ensures the load-balancer layer is discoverable.</summary>
        public static void Enable() { _ = typeof(LoadBalancerChannelService); }
    }
}
