using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Relay
{
    /// <summary>Reserved wire types for the relay. Don't reuse these ids for application messages.</summary>
    public static class RelayTypes
    {
        /// <summary>Client → server: allocate/join/leave/data command.</summary>
        public const ushort Command = ushort.MaxValue - 35;   // 65500

        /// <summary>Server → client: correlated reply to an allocate/join/leave command.</summary>
        public const ushort Reply = ushort.MaxValue - 36;     // 65499

        /// <summary>Server → client: push event (peer joined/left, relayed data, session closed).</summary>
        public const ushort Event = ushort.MaxValue - 37;     // 65498
    }

    internal enum RelayOp : byte { Allocate = 0, Join = 1, Leave = 2, Data = 3 }
    internal enum RelayEventType : byte { PeerJoined = 0, PeerLeft = 1, Data = 2, Closed = 3 }

    /// <summary>Thrown when a relay allocate/join fails (unknown code, full, timeout).</summary>
    public sealed class RelayException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public RelayException(string message) : base(message) { }
    }

    // ---- wire ----

    internal sealed class RelayCommand
    {
        public int CorrelationId;
        public RelayOp Op;
        public string Code = "";
        public int MaxPeers;
        public uint Target;
        public byte[] Payload = Array.Empty<byte>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write((byte)Op);
            w.Write(Code ?? "");
            w.Write(MaxPeers);
            w.Write(Target);
            w.Write(Payload?.Length ?? 0);
            if (Payload != null) w.Write(Payload);
            return ms.ToArray();
        }

        public static RelayCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var cmd = new RelayCommand
            {
                CorrelationId = r.ReadInt32(),
                Op = (RelayOp)r.ReadByte(),
                Code = r.ReadString(),
                MaxPeers = r.ReadInt32(),
                Target = r.ReadUInt32(),
            };
            var len = r.ReadInt32();
            cmd.Payload = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return cmd;
        }
    }

    internal sealed class RelayReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string Code = "";
        public uint OwnId;
        public uint[] Members = Array.Empty<uint>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(Code ?? "");
            w.Write(OwnId);
            w.Write(Members.Length);
            foreach (var m in Members) w.Write(m);
            return ms.ToArray();
        }

        public static RelayReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new RelayReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
                Code = r.ReadString(),
                OwnId = r.ReadUInt32(),
            };
            var count = r.ReadInt32();
            reply.Members = new uint[count];
            for (var i = 0; i < count; i++) reply.Members[i] = r.ReadUInt32();
            return reply;
        }
    }

    internal sealed class RelayEvent
    {
        public RelayEventType Type;
        public string Code = "";
        public uint FromPeerId;
        public byte[] Payload = Array.Empty<byte>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)Type);
            w.Write(Code ?? "");
            w.Write(FromPeerId);
            w.Write(Payload?.Length ?? 0);
            if (Payload != null) w.Write(Payload);
            return ms.ToArray();
        }

        public static RelayEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var evt = new RelayEvent
            {
                Type = (RelayEventType)r.ReadByte(),
                Code = r.ReadString(),
                FromPeerId = r.ReadUInt32(),
            };
            var len = r.ReadInt32();
            evt.Payload = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return evt;
        }
    }

    // ---- client-side plumbing ----

    internal static class RelayRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<RelayReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<RelayReply>>();
        private static readonly ConcurrentDictionary<RelayClient, byte> Clients
            = new ConcurrentDictionary<RelayClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<RelayReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, RelayReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(RelayClient c) => Clients[c] = 0;
        public static void DispatchEvent(RelayEvent evt) { foreach (var c in Clients.Keys) c.OnEvent(evt); }
    }

    /// <summary>
    /// Client-side relay driver, attached by <see cref="RelayClientExtensions.UseRelay"/>. Allocate a relay session (or
    /// join one by code), then push <b>opaque bytes</b> that the server forwards to the other members — a TURN-style
    /// fallback for peers that can't connect directly (symmetric NAT), or a simple hub for tunnelling any payload.
    /// </summary>
    public sealed class RelayClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string? _code;
        private uint _ownId;
        private readonly HashSet<uint> _members = new HashSet<uint>();

        /// <summary>This client's peer id within the current session (0 if not in one).</summary>
        public uint OwnId { get { lock (_gate) return _ownId; } }

        /// <summary>The current session code, or null.</summary>
        public string? Code { get { lock (_gate) return _code; } }

        /// <summary>Raised when another peer joins the session (arg: their peer id).</summary>
        public event Action<uint>? PeerJoined;

        /// <summary>Raised when a peer leaves the session (arg: their peer id).</summary>
        public event Action<uint>? PeerLeft;

        /// <summary>Raised for each relayed payload (args: sender peer id, opaque bytes).</summary>
        public event Action<uint, byte[]>? Received;

        /// <summary>Raised when the session closes.</summary>
        public event Action? Closed;

        internal RelayClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            RelayRegistry.RegisterClient(this);
        }

        /// <summary>Allocates a new relay session and joins it; returns the join code others use to connect.</summary>
        public async Task<string> AllocateAsync(int maxPeers = 0)
        {
            var reply = await SendCommand(RelayOp.Allocate, "", maxPeers, 0, Array.Empty<byte>()).ConfigureAwait(false);
            Apply(reply);
            return reply.Code;
        }

        /// <summary>Joins an existing relay session by code; throws <see cref="RelayException"/> if it's missing or full.</summary>
        public async Task JoinAsync(string code)
        {
            var reply = await SendCommand(RelayOp.Join, code, 0, 0, Array.Empty<byte>()).ConfigureAwait(false);
            Apply(reply);
        }

        /// <summary>Leaves the current session. Tolerant of a dropped connection (the server auto-removes us).</summary>
        public async Task LeaveAsync()
        {
            try { await SendCommand(RelayOp.Leave, "", 0, 0, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { /* already disconnected — server cleans up */ }
            lock (_gate) { _code = null; _members.Clear(); _ownId = 0; }
        }

        /// <summary>Forwards opaque bytes to every other member of the session.</summary>
        public Task SendAsync(byte[] data, DeliveryMethod delivery = DeliveryMethod.Reliable) => SendData(0, data, delivery);

        /// <summary>Forwards opaque bytes to a single member by peer id.</summary>
        public Task SendToAsync(uint peerId, byte[] data, DeliveryMethod delivery = DeliveryMethod.Reliable) => SendData(peerId, data, delivery);

        private Task SendData(uint target, byte[] data, DeliveryMethod delivery)
        {
            var cmd = new RelayCommand { CorrelationId = 0, Op = RelayOp.Data, Target = target, Payload = data ?? Array.Empty<byte>() };
            return _client.SendAsync(RelayTypes.Command, cmd.Encode(), delivery);
        }

        private async Task<RelayReply> SendCommand(RelayOp op, string code, int maxPeers, uint target, byte[] payload)
        {
            var id = RelayRegistry.NextId();
            var tcs = new TaskCompletionSource<RelayReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            RelayRegistry.Register(id, tcs);
            try
            {
                var cmd = new RelayCommand { CorrelationId = id, Op = op, Code = code, MaxPeers = maxPeers, Target = target, Payload = payload };
                await _client.SendAsync(RelayTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new RelayException("Relay command timed out."); }
                }
            }
            finally { RelayRegistry.Remove(id); }
        }

        private void Apply(RelayReply reply)
        {
            if (!reply.Success) throw new RelayException(reply.Error);
            lock (_gate)
            {
                _code = reply.Code;
                _ownId = reply.OwnId;
                _members.Clear();
                foreach (var m in reply.Members) _members.Add(m);
            }
        }

        internal void OnEvent(RelayEvent evt)
        {
            lock (_gate) { if (_code == null || _code != evt.Code) return; }   // not my session
            switch (evt.Type)
            {
                case RelayEventType.PeerJoined:
                    lock (_gate) _members.Add(evt.FromPeerId);
                    PeerJoined?.Invoke(evt.FromPeerId);
                    break;
                case RelayEventType.PeerLeft:
                    lock (_gate) _members.Remove(evt.FromPeerId);
                    PeerLeft?.Invoke(evt.FromPeerId);
                    break;
                case RelayEventType.Data:
                    Received?.Invoke(evt.FromPeerId, evt.Payload);
                    break;
                case RelayEventType.Closed:
                    lock (_gate) { _code = null; _members.Clear(); _ownId = 0; }
                    Closed?.Invoke();
                    break;
            }
        }
    }

    // ---- server-side ----

    internal sealed class RelaySession
    {
        public string Code = "";
        public int MaxPeers;
        public int NextPeerId;
        public readonly ConcurrentDictionary<uint, BasePeer> Members = new ConcurrentDictionary<uint, BasePeer>();
        public bool IsFull => MaxPeers > 0 && Members.Count >= MaxPeers;
    }

    internal sealed class RelayServerState
    {
        private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
        public readonly ConcurrentDictionary<string, RelaySession> Sessions = new ConcurrentDictionary<string, RelaySession>();
        // Where each live peer sits: peer id (Guid) -> (session code, peer id within session).
        public readonly ConcurrentDictionary<Guid, (string Code, uint PeerId)> Located = new ConcurrentDictionary<Guid, (string, uint)>();

        public RelaySession Allocate(int maxPeers)
        {
            while (true)
            {
                var code = GenerateCode();
                var session = new RelaySession { Code = code, MaxPeers = maxPeers };
                if (Sessions.TryAdd(code, session)) return session;
            }
        }

        private static string GenerateCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[6];
            for (var i = 0; i < 6; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }
    }

    /// <summary>Server-side relay hub. Enable with <see cref="RelayServerExtensions.UseRelay"/>.</summary>
    public static class RelayServer
    {
        private static readonly ConcurrentDictionary<BaseServer, RelayServerState> Servers = new ConcurrentDictionary<BaseServer, RelayServerState>();

        internal static void Enable(BaseServer server)
        {
            var state = Servers.GetOrAdd(server, _ => new RelayServerState());
            server.PeerDisconnected += peer => RemovePeer(state, peer);
        }

        internal static async Task OnCommand(BasePeer peer, RelayCommand cmd)
        {
            var server = peer.CurrentPeerInfo.Server;
            if (server == null || !Servers.TryGetValue(server, out var state)) return;

            switch (cmd.Op)
            {
                case RelayOp.Allocate:
                {
                    var session = state.Allocate(cmd.MaxPeers);
                    var id = Add(state, session, peer);
                    await Reply(peer, cmd.CorrelationId, true, "", session.Code, id, Array.Empty<uint>()).ConfigureAwait(false);
                    break;
                }
                case RelayOp.Join:
                {
                    if (!state.Sessions.TryGetValue(cmd.Code ?? "", out var session))
                    { await Reply(peer, cmd.CorrelationId, false, "No such relay session.", "", 0, Array.Empty<uint>()).ConfigureAwait(false); break; }
                    if (session.IsFull)
                    { await Reply(peer, cmd.CorrelationId, false, "Relay session is full.", "", 0, Array.Empty<uint>()).ConfigureAwait(false); break; }

                    var existing = new List<uint>(session.Members.Keys);
                    var id = Add(state, session, peer);
                    await Reply(peer, cmd.CorrelationId, true, "", session.Code, id, existing.ToArray()).ConfigureAwait(false);
                    await NotifyOthers(session, id, new RelayEvent { Type = RelayEventType.PeerJoined, Code = session.Code, FromPeerId = id }).ConfigureAwait(false);
                    break;
                }
                case RelayOp.Leave:
                    RemovePeer(state, peer);
                    await Reply(peer, cmd.CorrelationId, true, "", "", 0, Array.Empty<uint>()).ConfigureAwait(false);
                    break;
                case RelayOp.Data:
                {
                    if (!state.Located.TryGetValue(peer.CurrentPeerInfo.Id, out var loc)) break;
                    if (!state.Sessions.TryGetValue(loc.Code, out var session)) break;
                    var evt = new RelayEvent { Type = RelayEventType.Data, Code = session.Code, FromPeerId = loc.PeerId, Payload = cmd.Payload };
                    if (cmd.Target == 0) await NotifyOthers(session, loc.PeerId, evt).ConfigureAwait(false);
                    else if (session.Members.TryGetValue(cmd.Target, out var target)) await Send(target, evt).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static uint Add(RelayServerState state, RelaySession session, BasePeer peer)
        {
            var id = (uint)Interlocked.Increment(ref session.NextPeerId);
            session.Members[id] = peer;
            state.Located[peer.CurrentPeerInfo.Id] = (session.Code, id);
            return id;
        }

        private static void RemovePeer(RelayServerState state, BasePeer peer)
        {
            if (!state.Located.TryRemove(peer.CurrentPeerInfo.Id, out var loc)) return;
            if (!state.Sessions.TryGetValue(loc.Code, out var session)) return;
            session.Members.TryRemove(loc.PeerId, out _);
            if (session.Members.IsEmpty) { state.Sessions.TryRemove(session.Code, out _); return; }
            _ = NotifyOthers(session, loc.PeerId, new RelayEvent { Type = RelayEventType.PeerLeft, Code = session.Code, FromPeerId = loc.PeerId });
        }

        private static async Task NotifyOthers(RelaySession session, uint except, RelayEvent evt)
        {
            // Sent via SendAsync (serializer-wrapped) to match the auto-discovered IClientMessageHandler<byte[]> invoker.
            var encoded = evt.Encode();
            foreach (var kv in session.Members)
            {
                if (kv.Key == except) continue;
                try { await kv.Value.SendAsync(RelayTypes.Event, encoded, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropped */ }
            }
        }

        private static Task Send(BasePeer peer, RelayEvent evt)
        {
            try { return peer.SendAsync(RelayTypes.Event, evt.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string err, string code, uint ownId, uint[] members)
        {
            var reply = new RelayReply { CorrelationId = corr, Success = ok, Error = err, Code = code, OwnId = ownId, Members = members };
            return peer.SendAsync(RelayTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
        }
    }

    /// <summary>Auto-discovered server handler for relay commands.</summary>
    [MessageHandler(RelayTypes.Command)]
    public sealed class RelayCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data) => RelayServer.OnCommand(peer, RelayCommand.Decode(data));
    }

    /// <summary>Auto-discovered client handler for correlated relay replies.</summary>
    [MessageHandler(RelayTypes.Reply)]
    public sealed class RelayReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = RelayReply.Decode(data); RelayRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for relay push events.</summary>
    [MessageHandler(RelayTypes.Event)]
    public sealed class RelayEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { RelayRegistry.DispatchEvent(RelayEvent.Decode(data)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the relay hub to a server by composition.</summary>
    public static class RelayServerExtensions
    {
        /// <summary>Enables the server-side relay hub.</summary>
        public static void UseRelay(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            RelayServer.Enable(server);
        }
    }

    /// <summary>Attaches a relay driver to a client by composition.</summary>
    public static class RelayClientExtensions
    {
        /// <summary>Enables client-side relay; returns the driver (allocate/join/leave/send + events).</summary>
        public static RelayClient UseRelay(this BaseClient client) => new RelayClient(client);
    }

    /// <summary>One-time bootstrap so the relay handlers are discovered. Call at startup.</summary>
    public static class RelayRuntime
    {
        /// <summary>Ensures the relay layer is discoverable.</summary>
        public static void Enable() { _ = RelayTypes.Command; }
    }
}
