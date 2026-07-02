using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Protocol;

namespace SetNet.Relay
{
    /// <summary>Command operations (client → server) within the Relay protocol channel.</summary>
    internal enum RelayOp : ushort
    {
        /// <summary>Allocate a new relay session and join it.</summary>
        Allocate = 1,
        /// <summary>Join an existing relay session by code.</summary>
        Join = 2,
        /// <summary>Leave the current session.</summary>
        Leave = 3,
        /// <summary>Forward opaque bytes to other members (fire-and-forget).</summary>
        Data = 4,
    }

    /// <summary>Push events (server → client) within the Relay protocol channel.</summary>
    internal enum RelayEvt : ushort
    {
        /// <summary>Another peer joined the session.</summary>
        PeerJoined = 10,
        /// <summary>A peer left the session.</summary>
        PeerLeft = 11,
        /// <summary>Relayed opaque data from another member.</summary>
        Data = 12,
        /// <summary>The session closed.</summary>
        Closed = 13,
    }

    /// <summary>Thrown when a relay allocate/join fails (unknown code, full, timeout).</summary>
    public sealed class RelayException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public RelayException(string message) : base(message) { }
    }

    // ---- wire ----

    /// <summary>
    /// Body codecs for the Relay channel. The unified protocol envelope already carries kind/channel/op/correlation,
    /// so these encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic.
    /// </summary>
    internal static class RelayWire
    {
        /// <summary>Allocate-command body: the requested max peers.</summary>
        public static byte[] EncodeAllocate(int maxPeers)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) w.Write(maxPeers);
            return ms.ToArray();
        }

        /// <summary>Reads an allocate-command body.</summary>
        public static int DecodeAllocate(byte[] body)
        {
            if (body == null || body.Length < 4) return 0;
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return r.ReadInt32();
        }

        /// <summary>Join-command body: the session code.</summary>
        public static byte[] EncodeJoin(string code)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) w.Write(code ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a join-command body.</summary>
        public static string DecodeJoin(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }

        /// <summary>Data-command body: [uint target (0 = all)][payload].</summary>
        public static byte[] EncodeData(uint target, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(target);
            w.Write(payload?.Length ?? 0);
            if (payload != null) w.Write(payload);
            return ms.ToArray();
        }

        /// <summary>Reads a data-command body.</summary>
        public static (uint target, byte[] payload) DecodeData(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var target = r.ReadUInt32();
            var len = r.ReadInt32();
            var payload = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return (target, payload);
        }

        /// <summary>Allocate/Join reply body: the session code, the caller's peer id, and the current member list.</summary>
        public static byte[] EncodeReply(string code, uint ownId, uint[] members)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(code ?? "");
            w.Write(ownId);
            w.Write(members?.Length ?? 0);
            if (members != null) foreach (var m in members) w.Write(m);
            return ms.ToArray();
        }

        /// <summary>Reads an Allocate/Join reply body.</summary>
        public static (string code, uint ownId, uint[] members) DecodeReply(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var code = r.ReadString();
            var ownId = r.ReadUInt32();
            var count = r.ReadInt32();
            var members = new uint[count];
            for (var i = 0; i < count; i++) members[i] = r.ReadUInt32();
            return (code, ownId, members);
        }

        /// <summary>Peer-joined/left event body: [session code][peer id].</summary>
        public static byte[] EncodePeer(string code, uint peerId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(code ?? "");
            w.Write(peerId);
            return ms.ToArray();
        }

        /// <summary>Reads a peer-joined/left event body.</summary>
        public static (string code, uint peerId) DecodePeer(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return (r.ReadString(), r.ReadUInt32());
        }

        /// <summary>Data event body: [session code][from peer id][payload].</summary>
        public static byte[] EncodeDataEvent(string code, uint fromPeerId, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(code ?? "");
            w.Write(fromPeerId);
            w.Write(payload?.Length ?? 0);
            if (payload != null) w.Write(payload);
            return ms.ToArray();
        }

        /// <summary>Reads a data event body.</summary>
        public static (string code, uint fromPeerId, byte[] payload) DecodeDataEvent(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var code = r.ReadString();
            var fromPeerId = r.ReadUInt32();
            var len = r.ReadInt32();
            var payload = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return (code, fromPeerId, payload);
        }

        /// <summary>Session-closed event body: [session code].</summary>
        public static byte[] EncodeClosed(string code)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) w.Write(code ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a session-closed event body.</summary>
        public static string DecodeClosed(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }
    }

    // ---- client ----

    /// <summary>
    /// Client-side relay driver, attached by <see cref="RelayClientExtensions.UseRelay"/>. Allocate a relay session (or
    /// join one by code), then push <b>opaque bytes</b> that the server forwards to the other members — a TURN-style
    /// fallback for peers that can't connect directly (symmetric NAT), or a simple hub for tunnelling any payload.
    /// Rides the unified protocol on the <see cref="Channels.Relay"/> channel.
    /// </summary>
    public sealed class RelayClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string? _code;
        private uint _ownId;
        private readonly HashSet<uint> _members = new HashSet<uint>();
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

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
            _subscriptions.Add(_client.OnRaw(Channels.Relay, (ushort)RelayEvt.PeerJoined, OnPeerJoinedEvent));
            _subscriptions.Add(_client.OnRaw(Channels.Relay, (ushort)RelayEvt.PeerLeft, OnPeerLeftEvent));
            _subscriptions.Add(_client.OnRaw(Channels.Relay, (ushort)RelayEvt.Data, OnDataEvent));
            _subscriptions.Add(_client.OnRaw(Channels.Relay, (ushort)RelayEvt.Closed, OnClosedEvent));
        }

        /// <summary>Allocates a new relay session and joins it; returns the join code others use to connect.</summary>
        public async Task<string> AllocateAsync(int maxPeers = 0)
        {
            var (code, ownId, members) = ApplyReply(await RequestAsync((ushort)RelayOp.Allocate, RelayWire.EncodeAllocate(maxPeers)).ConfigureAwait(false));
            _ = ownId; _ = members;
            return code;
        }

        /// <summary>Joins an existing relay session by code; throws <see cref="RelayException"/> if it's missing or full.</summary>
        public async Task JoinAsync(string code)
            => ApplyReply(await RequestAsync((ushort)RelayOp.Join, RelayWire.EncodeJoin(code)).ConfigureAwait(false));

        /// <summary>Leaves the current session. Tolerant of a dropped connection (the server auto-removes us).</summary>
        public async Task LeaveAsync()
        {
            try { await RequestAsync((ushort)RelayOp.Leave, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { /* already disconnected — server cleans up */ }
            lock (_gate) { _code = null; _members.Clear(); _ownId = 0; }
        }

        /// <summary>Forwards opaque bytes to every other member of the session.</summary>
        public Task SendAsync(byte[] data, DeliveryMethod delivery = DeliveryMethod.Reliable) => SendData(0, data, delivery);

        /// <summary>Forwards opaque bytes to a single member by peer id.</summary>
        public Task SendToAsync(uint peerId, byte[] data, DeliveryMethod delivery = DeliveryMethod.Reliable) => SendData(peerId, data, delivery);

        private Task SendData(uint target, byte[] data, DeliveryMethod delivery)
            => _client.PostRawAsync(Channels.Relay, (ushort)RelayOp.Data, RelayWire.EncodeData(target, data ?? Array.Empty<byte>()), delivery);

        /// <summary>Sends a relay command and maps protocol failures back to the public <see cref="RelayException"/>.</summary>
        private async Task<byte[]> RequestAsync(ushort op, byte[] body)
        {
            try { return await _client.RequestRawAsync(Channels.Relay, op, body).ConfigureAwait(false); }
            catch (ProtocolException ex) { throw new RelayException(ex.Message); }
            catch (TimeoutException) { throw new RelayException("Relay command timed out."); }
        }

        private (string code, uint ownId, uint[] members) ApplyReply(byte[] replyBody)
        {
            var (code, ownId, members) = RelayWire.DecodeReply(replyBody);
            lock (_gate)
            {
                _code = code;
                _ownId = ownId;
                _members.Clear();
                foreach (var m in members) _members.Add(m);
            }
            return (code, ownId, members);
        }

        private void OnPeerJoinedEvent(byte[] body)
        {
            var (code, peerId) = RelayWire.DecodePeer(body);
            lock (_gate) { if (_code == null || _code != code) return; _members.Add(peerId); }   // not my session
            PeerJoined?.Invoke(peerId);
        }

        private void OnPeerLeftEvent(byte[] body)
        {
            var (code, peerId) = RelayWire.DecodePeer(body);
            lock (_gate) { if (_code == null || _code != code) return; _members.Remove(peerId); }   // not my session
            PeerLeft?.Invoke(peerId);
        }

        private void OnDataEvent(byte[] body)
        {
            var (code, fromPeerId, payload) = RelayWire.DecodeDataEvent(body);
            lock (_gate) { if (_code == null || _code != code) return; }   // not my session
            Received?.Invoke(fromPeerId, payload);
        }

        private void OnClosedEvent(byte[] body)
        {
            var code = RelayWire.DecodeClosed(body);
            lock (_gate) { if (_code == null || _code != code) return; _code = null; _members.Clear(); _ownId = 0; }
            Closed?.Invoke();
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

        internal static RelayServerState? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var state) ? state : null;

        internal static async Task OnCommand(ChannelRequest request, RelayServerState state)
        {
            var peer = request.Peer;
            switch ((RelayOp)request.Op)
            {
                case RelayOp.Allocate:
                {
                    var session = state.Allocate(RelayWire.DecodeAllocate(request.RawBody));
                    var id = Add(state, session, peer);
                    await request.ReplyRawAsync(RelayWire.EncodeReply(session.Code, id, Array.Empty<uint>())).ConfigureAwait(false);
                    break;
                }
                case RelayOp.Join:
                {
                    var code = RelayWire.DecodeJoin(request.RawBody);
                    if (!state.Sessions.TryGetValue(code ?? "", out var session)) throw new ProtocolException("No such relay session.");
                    if (session.IsFull) throw new ProtocolException("Relay session is full.");

                    var existing = new List<uint>(session.Members.Keys);
                    var id = Add(state, session, peer);
                    await request.ReplyRawAsync(RelayWire.EncodeReply(session.Code, id, existing.ToArray())).ConfigureAwait(false);
                    await NotifyOthers(session, id, (ushort)RelayEvt.PeerJoined, RelayWire.EncodePeer(session.Code, id)).ConfigureAwait(false);
                    break;
                }
                case RelayOp.Leave:
                    RemovePeer(state, peer);
                    if (request.ExpectsReply) await request.ReplyRawAsync(Array.Empty<byte>()).ConfigureAwait(false);
                    break;
                case RelayOp.Data:
                {
                    if (!state.Located.TryGetValue(peer.CurrentPeerInfo.Id, out var loc)) break;
                    if (!state.Sessions.TryGetValue(loc.Code, out var session)) break;
                    var (target, payload) = RelayWire.DecodeData(request.RawBody);
                    var body = RelayWire.EncodeDataEvent(session.Code, loc.PeerId, payload);
                    if (target == 0) await NotifyOthers(session, loc.PeerId, (ushort)RelayEvt.Data, body).ConfigureAwait(false);
                    else if (session.Members.TryGetValue(target, out var to)) await Send(to, (ushort)RelayEvt.Data, body).ConfigureAwait(false);
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
            _ = NotifyOthers(session, loc.PeerId, (ushort)RelayEvt.PeerLeft, RelayWire.EncodePeer(session.Code, loc.PeerId));
        }

        private static async Task NotifyOthers(RelaySession session, uint except, ushort evtOp, byte[] body)
        {
            // Pushed via PublishRawAsync (which SendAsyncs the envelope byte[]) so the client's OnRaw subscription decodes it.
            foreach (var kv in session.Members)
            {
                if (kv.Key == except) continue;
                try { await kv.Value.PublishRawAsync(Channels.Relay, evtOp, body).ConfigureAwait(false); } catch { /* dropped */ }
            }
        }

        private static Task Send(BasePeer peer, ushort evtOp, byte[] body)
        {
            try { return peer.PublishRawAsync(Channels.Relay, evtOp, body); } catch { return Task.CompletedTask; }
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>
    /// Auto-discovered channel service for relay commands (allocate/join/leave/data). Replaces the former hand-framed
    /// <c>[MessageHandler]</c> classes and correlation plumbing: the unified protocol handles correlation and reply
    /// framing, so this only implements the relay logic and dispatches on the op.
    /// </summary>
    [ProtocolChannel(Channels.Relay)]
    public sealed class RelayChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var state = RelayServer.For(request.Peer.CurrentPeerInfo.Server);
            if (state == null) throw new ProtocolException("relay is not configured on this server");
            return RelayServer.OnCommand(request, state);
        }
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

    /// <summary>One-time bootstrap so the relay channel service is discovered. Call at startup.</summary>
    public static class RelayRuntime
    {
        /// <summary>Ensures the relay layer is discoverable.</summary>
        public static void Enable() { _ = typeof(RelayChannelService); }
    }
}
