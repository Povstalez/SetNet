using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;
using SetNet.Messaging;

namespace SetNet.Lockstep
{
    /// <summary>Command operations (client → server) within the Lockstep protocol channel (fire-and-forget).</summary>
    internal enum LockstepOp : ushort { Input = 1 }

    /// <summary>Push events (server → client) within the Lockstep protocol channel.</summary>
    internal enum LockstepEvt : ushort { Turn = 10 }

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
    /// Rides the unified protocol on the <see cref="Channels.Lockstep"/> channel.
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
            try { await peer.PublishRawAsync(Channels.Lockstep, (ushort)LockstepEvt.Turn, frame).ConfigureAwait(false); } catch { /* dropping */ }
        }

        /// <inheritdoc/>
        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>
    /// Client-side lockstep driver over a **typed** per-turn input. Submit your input type directly and receive each
    /// player's deserialized input — the driver (de)serializes via the registered <see cref="SetNetSerializer"/>, so you
    /// never touch raw bytes. (The server relays inputs opaquely and stays serializer-agnostic.)
    /// </summary>
    /// <typeparam name="TInput">Your per-turn input type.</typeparam>
    public sealed class ClientLockstep<TInput>
    {
        private readonly BaseClient _client;
        private readonly IDisposable _subscription;
        private uint _nextTurn;

        /// <summary>Raised when a turn is finalized (args: turn number, map of player id → that player's deserialized input). Advance your deterministic simulation here.</summary>
        public event Action<uint, IReadOnlyDictionary<string, TInput>>? TurnReady;

        internal ClientLockstep(BaseClient client)
        {
            _client = client;
            _subscription = _client.OnRaw(Channels.Lockstep, (ushort)LockstepEvt.Turn, body =>
            {
                var (turn, inputs) = LockstepWire.DecodeTurn(body);
                OnTurn(turn, inputs);
            });
        }

        /// <summary>Submits this client's input for the next turn (serialized via <see cref="SetNetSerializer"/>). Returns the turn number it was tagged with.</summary>
        public uint SubmitInput(TInput input)
        {
            var turn = _nextTurn;
            _ = SafeSend(LockstepWire.EncodeInput(turn, SetNetSerializer.Serialize(input)));
            return turn;
        }

        private void OnTurn(uint turn, Dictionary<string, byte[]> inputs)
        {
            if (turn >= _nextTurn) _nextTurn = turn + 1;
            var handler = TurnReady;
            if (handler == null) return;
            var typed = new Dictionary<string, TInput>(inputs.Count);
            foreach (var kv in inputs) typed[kv.Key] = SetNetSerializer.Deserialize<TInput>(kv.Value);
            handler(turn, typed);
        }

        private async Task SafeSend(byte[] frame)
        {
            try { await _client.PostRawAsync(Channels.Lockstep, (ushort)LockstepOp.Input, frame).ConfigureAwait(false); } catch { /* dropping */ }
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
        public static void RegisterServer(BaseServer server, ServerLockstep engine) => Servers[server] = engine;
        public static ServerLockstep? GetServer(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;
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

        /// <summary>Enables the client-side lockstep driver over a typed input (SubmitInput + TurnReady). Use <c>byte[]</c> as <typeparamref name="TInput"/> for a raw payload.</summary>
        public static ClientLockstep<TInput> UseLockstep<TInput>(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new ClientLockstep<TInput>(client);
        }
    }

    /// <summary>Auto-discovered channel service for lockstep inputs (fire-and-forget).</summary>
    [ProtocolChannel(Channels.Lockstep)]
    public sealed class LockstepChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var engine = LockstepRegistry.GetServer(request.Peer.CurrentPeerInfo.Server);
            if (engine != null && (LockstepOp)request.Op == LockstepOp.Input)
            {
                var (turn, payload) = LockstepWire.DecodeInput(request.RawBody);
                engine.OnInput(request.Peer, turn, payload);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time bootstrap so the lockstep channel service is discovered. Call at startup.</summary>
    public static class LockstepRuntime
    {
        /// <summary>Ensures the lockstep layer is discoverable.</summary>
        public static void Enable() { _ = typeof(LockstepChannelService); }
    }
}
