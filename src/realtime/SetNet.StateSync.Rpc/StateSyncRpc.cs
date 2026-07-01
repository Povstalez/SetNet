using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.StateSync.Rpc
{
    /// <summary>Reserved wire type for entity-scoped RPCs (below the fragmentation/state-sync range). Don't reuse.</summary>
    public static class StateRpcTypes
    {
        /// <summary>An entity RPC: <c>[4 netId][2 methodId][4 len][payload]</c>.</summary>
        public const ushort Rpc = ushort.MaxValue - 19;   // 65516
    }

    internal static class StateRpcWire
    {
        public static byte[] Encode(uint netId, ushort methodId, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var frame = new byte[10 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), netId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), methodId);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6, 4), payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            return frame;
        }

        public static (uint netId, ushort methodId, byte[] payload) Decode(byte[] frame)
        {
            var netId = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, 4));
            var methodId = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2));
            var len = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(6, 4));
            var payload = new byte[len];
            Buffer.BlockCopy(frame, 10, payload, 0, len);
            return (netId, methodId, payload);
        }
    }

    /// <summary>
    /// Entity-scoped RPCs on top of state replication: send a method call tagged with a <c>NetId</c> to a specific client
    /// (server → client) or to the server (client → owned entity). It's a thin, targeted message channel — you dispatch
    /// <c>methodId</c> to your own method on the matching object. Reliable by default.
    /// </summary>
    public sealed class ServerStateRpc
    {
        /// <summary>Raised when a client invokes an RPC (args: sender peer, entity net id, method id, payload).</summary>
        public event Action<BasePeer, uint, ushort, byte[]>? Received;

        internal void Raise(BasePeer peer, uint netId, ushort methodId, byte[] payload) => Received?.Invoke(peer, netId, methodId, payload);

        /// <summary>Sends an entity RPC to one client (e.g. the entity's owner or an observer).</summary>
        public Task SendAsync(BasePeer peer, uint netId, ushort methodId, byte[] payload, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => peer.SendAsync(StateRpcTypes.Rpc, StateRpcWire.Encode(netId, methodId, payload), delivery);
    }

    /// <summary>Client-side entity RPC channel: invoke on an owned entity (client → server) and receive server → client calls.</summary>
    public sealed class ClientStateRpc
    {
        private readonly BaseClient _client;

        /// <summary>Raised when the server invokes an RPC on an entity (args: entity net id, method id, payload).</summary>
        public event Action<uint, ushort, byte[]>? Received;

        internal ClientStateRpc(BaseClient client)
        {
            _client = client;
            StateRpcRegistry.RegisterClient(this);
        }

        internal void Raise(uint netId, ushort methodId, byte[] payload) => Received?.Invoke(netId, methodId, payload);

        /// <summary>Sends an entity RPC to the server (typically for the entity you own).</summary>
        public Task SendAsync(uint netId, ushort methodId, byte[] payload, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => _client.SendAsync(StateRpcTypes.Rpc, StateRpcWire.Encode(netId, methodId, payload), delivery);
    }

    internal static class StateRpcRegistry
    {
        private static readonly ConcurrentDictionary<BaseServer, ServerStateRpc> Servers = new ConcurrentDictionary<BaseServer, ServerStateRpc>();
        private static readonly ConcurrentDictionary<ClientStateRpc, byte> Clients = new ConcurrentDictionary<ClientStateRpc, byte>();

        public static ServerStateRpc GetOrAddServer(BaseServer server) => Servers.GetOrAdd(server, _ => new ServerStateRpc());
        public static ServerStateRpc? GetServer(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;
        public static void RegisterClient(ClientStateRpc client) => Clients[client] = 0;
        public static void ForEachClient(Action<ClientStateRpc> action) { foreach (var c in Clients.Keys) action(c); }
    }

    /// <summary>Attaches entity RPCs by composition — no base class.</summary>
    public static class StateSyncRpcExtensions
    {
        /// <summary>Enables server-side entity RPCs and returns the channel (Received event + SendAsync to a peer).</summary>
        public static ServerStateRpc UseStateSyncRpc(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return StateRpcRegistry.GetOrAddServer(server);
        }

        /// <summary>Enables client-side entity RPCs and returns the channel (Received event + SendAsync to the server).</summary>
        public static ClientStateRpc UseStateSyncRpc(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new ClientStateRpc(client);
        }
    }

    /// <summary>Auto-discovered server handler for inbound entity RPCs.</summary>
    [MessageHandler(StateRpcTypes.Rpc)]
    public sealed class StateRpcServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var channel = StateRpcRegistry.GetServer(peer.CurrentPeerInfo.Server);
            if (channel != null) { var (netId, methodId, payload) = StateRpcWire.Decode(data); channel.Raise(peer, netId, methodId, payload); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for inbound entity RPCs.</summary>
    [MessageHandler(StateRpcTypes.Rpc)]
    public sealed class StateRpcClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var (netId, methodId, payload) = StateRpcWire.Decode(data);
            StateRpcRegistry.ForEachClient(c => c.Raise(netId, methodId, payload));
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the entity-RPC handlers are discovered. Call at startup.</summary>
    public static class StateSyncRpcRuntime
    {
        /// <summary>Ensures the entity-RPC layer is discoverable.</summary>
        public static void Enable() { _ = StateRpcTypes.Rpc; }
    }
}
