using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Lockstep
{
    /// <summary>Reserved wire types for the lockstep protocol (below the entity-RPC range). Don't reuse.</summary>
    public static class LockstepTypes
    {
        /// <summary>Client → server: an input for a specific turn.</summary>
        public const ushort Input = ushort.MaxValue - 21;   // 65514

        /// <summary>Server → client: the finalized input set for a turn.</summary>
        public const ushort Turn = ushort.MaxValue - 20;    // 65515
    }

    /// <summary>Tunables for the lockstep engine.</summary>
    public sealed class LockstepOptions
    {
        /// <summary>Maximum time (ms) to wait for every participant's input before finalizing a turn anyway (dropping laggards). Default 200.</summary>
        public int TurnTimeoutMs { get; set; } = 200;
    }

    /// <summary>
    /// A deterministic **lockstep** engine: instead of streaming state, the server collects each participant's input for a
    /// turn and, once **all** inputs are in (or the turn times out), broadcasts the complete input set so every client
    /// advances its simulation identically. Ideal for RTS and other deterministic games where sending inputs is far cheaper
    /// than sending state. Determinism of the simulation itself is your responsibility (fixed-point/careful float use).
    /// </summary>
    public sealed class ServerLockstep : IDisposable
    {
        private readonly BaseServer _server;
        private readonly LockstepOptions _options;
        private readonly ConcurrentDictionary<BasePeer, Guid> _participants = new ConcurrentDictionary<BasePeer, Guid>();
        private readonly ConcurrentDictionary<uint, ConcurrentDictionary<Guid, byte[]>> _pending = new ConcurrentDictionary<uint, ConcurrentDictionary<Guid, byte[]>>();
        private readonly object _finalizeGate = new object();
        private uint _currentTurn;
        private Timer? _timer;

        internal ServerLockstep(BaseServer server, LockstepOptions options)
        {
            _server = server;
            _options = options;
            _server.PeerConnected += p => _participants[p] = p.CurrentPeerInfo.Id;
            _server.PeerDisconnected += p => _participants.TryRemove(p, out _);
            _timer = new Timer(_ => Finalize(force: true), null, options.TurnTimeoutMs, options.TurnTimeoutMs);
        }

        internal void OnInput(BasePeer peer, uint turn, byte[] payload)
        {
            if (turn < _currentTurn) return;   // input for an already-finalized turn — too late
            var bucket = _pending.GetOrAdd(turn, _ => new ConcurrentDictionary<Guid, byte[]>());
            bucket[peer.CurrentPeerInfo.Id] = payload;
            if (turn == _currentTurn && bucket.Count >= _participants.Count && _participants.Count > 0)
                Finalize(force: false);   // everyone's in — advance immediately
        }

        private void Finalize(bool force)
        {
            uint turn;
            ConcurrentDictionary<Guid, byte[]>? bucket;
            lock (_finalizeGate)
            {
                turn = _currentTurn;
                _pending.TryGetValue(turn, out bucket);
                var haveAll = bucket != null && _participants.Count > 0 && bucket.Count >= _participants.Count;
                if (!force && !haveAll) return;
                _currentTurn++;
                _pending.TryRemove(turn, out _);
            }

            var frame = LockstepWire.EncodeTurn(turn, bucket);
            foreach (var peer in _participants.Keys)
                _ = SafeSend(peer, frame);
        }

        private static async Task SafeSend(BasePeer peer, byte[] frame)
        {
            try { await peer.SendAsync(LockstepTypes.Turn, frame, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropping */ }
        }

        /// <inheritdoc/>
        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>Client-side lockstep driver: submit inputs and receive finalized turns.</summary>
    public sealed class ClientLockstep
    {
        private readonly BaseClient _client;
        private uint _nextTurn;

        /// <summary>Raised when a turn is finalized (args: turn number, map of player id → that player's input for the turn). Advance your deterministic simulation here.</summary>
        public event Action<uint, IReadOnlyDictionary<string, byte[]>>? TurnReady;

        internal ClientLockstep(BaseClient client)
        {
            _client = client;
            LockstepRegistry.RegisterClient(this);
        }

        /// <summary>Submits this client's input for the next turn. Returns the turn number it was tagged with.</summary>
        public uint SubmitInput(byte[] payload)
        {
            var turn = _nextTurn;
            _ = SafeSend(LockstepWire.EncodeInput(turn, payload ?? Array.Empty<byte>()));
            return turn;
        }

        internal void OnTurn(uint turn, IReadOnlyDictionary<string, byte[]> inputs)
        {
            if (turn >= _nextTurn) _nextTurn = turn + 1;
            TurnReady?.Invoke(turn, inputs);
        }

        private async Task SafeSend(byte[] frame)
        {
            try { await _client.SendAsync(LockstepTypes.Input, frame, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropping */ }
        }
    }

    internal static class LockstepWire
    {
        public static byte[] EncodeInput(uint turn, byte[] payload)
        {
            var frame = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), turn);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 8, payload.Length);
            return frame;
        }

        public static (uint turn, byte[] payload) DecodeInput(byte[] frame)
        {
            var turn = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4, 4));
            var payload = new byte[len];
            Buffer.BlockCopy(frame, 8, payload, 0, len);
            return (turn, payload);
        }

        public static byte[] EncodeTurn(uint turn, ConcurrentDictionary<Guid, byte[]>? inputs)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(turn);
                var count = inputs?.Count ?? 0;
                w.Write(count);
                if (inputs != null)
                    foreach (var kv in inputs)
                    {
                        w.Write(kv.Key.ToString("N"));
                        w.Write(kv.Value.Length);
                        w.Write(kv.Value);
                    }
            }
            return ms.ToArray();
        }

        public static (uint turn, Dictionary<string, byte[]> inputs) DecodeTurn(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var turn = r.ReadUInt32();
            var count = r.ReadInt32();
            var inputs = new Dictionary<string, byte[]>(count);
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadString();
                var len = r.ReadInt32();
                inputs[id] = r.ReadBytes(len);
            }
            return (turn, inputs);
        }
    }

    internal static class LockstepRegistry
    {
        private static readonly ConcurrentDictionary<BaseServer, ServerLockstep> Servers = new ConcurrentDictionary<BaseServer, ServerLockstep>();
        private static readonly ConcurrentDictionary<ClientLockstep, byte> Clients = new ConcurrentDictionary<ClientLockstep, byte>();
        public static void RegisterServer(BaseServer server, ServerLockstep engine) => Servers[server] = engine;
        public static ServerLockstep? GetServer(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;
        public static void RegisterClient(ClientLockstep client) => Clients[client] = 0;
        public static void ForEachClient(Action<ClientLockstep> action) { foreach (var c in Clients.Keys) action(c); }
    }

    /// <summary>Attaches the lockstep engine by composition — no base class.</summary>
    public static class LockstepExtensions
    {
        /// <summary>Enables the server-side lockstep engine (auto-enrolls connected peers as participants).</summary>
        public static ServerLockstep UseLockstep(this BaseServer server, LockstepOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var engine = new ServerLockstep(server, options ?? new LockstepOptions());
            LockstepRegistry.RegisterServer(server, engine);
            return engine;
        }

        /// <summary>Enables the client-side lockstep driver (SubmitInput + TurnReady).</summary>
        public static ClientLockstep UseLockstep(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new ClientLockstep(client);
        }
    }

    /// <summary>Auto-discovered server handler for lockstep inputs.</summary>
    [MessageHandler(LockstepTypes.Input)]
    public sealed class LockstepInputHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var engine = LockstepRegistry.GetServer(peer.CurrentPeerInfo.Server);
            if (engine != null) { var (turn, payload) = LockstepWire.DecodeInput(data); engine.OnInput(peer, turn, payload); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for finalized turns.</summary>
    [MessageHandler(LockstepTypes.Turn)]
    public sealed class LockstepTurnHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var (turn, inputs) = LockstepWire.DecodeTurn(data);
            LockstepRegistry.ForEachClient(c => c.OnTurn(turn, inputs));
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the lockstep handlers are discovered. Call at startup.</summary>
    public static class LockstepRuntime
    {
        /// <summary>Ensures the lockstep layer is discoverable.</summary>
        public static void Enable() { _ = LockstepTypes.Input; }
    }
}
