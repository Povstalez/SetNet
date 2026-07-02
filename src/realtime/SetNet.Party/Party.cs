using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Party
{
    /// <summary>Command operations (client → server) within the Party protocol channel.</summary>
    internal enum PartyOp : ushort { Create = 1, Join = 2, Leave = 3, SetReady = 4 }

    /// <summary>Push events (server → client) within the Party protocol channel.</summary>
    internal enum PartyEvt : ushort { Joined = 10, Left = 11, LeaderChanged = 12, ReadyChanged = 13, Disbanded = 14 }

    /// <summary>A member of a party as seen by a client.</summary>
    public sealed class PartyMember
    {
        /// <summary>The member's player id.</summary>
        public string PlayerId { get; }
        /// <summary>Whether the member has readied up.</summary>
        public bool Ready { get; }
        /// <summary>Creates a party member snapshot.</summary>
        public PartyMember(string playerId, bool ready) { PlayerId = playerId; Ready = ready; }
    }

    /// <summary>What a client knows about its party.</summary>
    public sealed class PartyInfo
    {
        /// <summary>The party's join code.</summary>
        public string Code { get; }
        /// <summary>This client's own player id.</summary>
        public string OwnPlayerId { get; }
        /// <summary>The party leader's player id.</summary>
        public string LeaderId { get; }
        /// <summary>The current members.</summary>
        public IReadOnlyList<PartyMember> Members { get; }
        /// <summary>Creates a party snapshot.</summary>
        public PartyInfo(string code, string ownId, string leaderId, IReadOnlyList<PartyMember> members)
        { Code = code; OwnPlayerId = ownId; LeaderId = leaderId; Members = members; }
    }

    /// <summary>Thrown when a party command is rejected.</summary>
    public class PartyException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public PartyException(string message) : base(message) { }
    }

    /// <summary>Body codecs for the Party channel (payload only; the envelope carries kind/channel/op/correlation).</summary>
    internal static class PartyWire
    {
        public static byte[] EncodeJoin(string code)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(code ?? "");
            return ms.ToArray();
        }

        public static string DecodeJoin(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }

        public static byte[] EncodeReady(bool ready)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(ready);
            return ms.ToArray();
        }

        public static bool DecodeReady(byte[] body)
        {
            if (body == null || body.Length == 0) return false;
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadBoolean();
        }

        public static byte[] EncodeReply(string code, string ownId, string leaderId, IReadOnlyList<PartyMember> members)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(code ?? "");
                w.Write(ownId ?? "");
                w.Write(leaderId ?? "");
                w.Write(members?.Count ?? 0);
                if (members != null) foreach (var m in members) { w.Write(m.PlayerId ?? ""); w.Write(m.Ready); }
            }
            return ms.ToArray();
        }

        public static PartyInfo DecodeReply(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var code = r.ReadString();
            var ownId = r.ReadString();
            var leaderId = r.ReadString();
            var count = r.ReadInt32();
            var members = new List<PartyMember>(count);
            for (var i = 0; i < count; i++) members.Add(new PartyMember(r.ReadString(), r.ReadBoolean()));
            return new PartyInfo(code, ownId, leaderId, members);
        }

        public static byte[] EncodeEvent(string code, string playerId, bool ready, string leaderId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(code ?? "");
                w.Write(playerId ?? "");
                w.Write(ready);
                w.Write(leaderId ?? "");
            }
            return ms.ToArray();
        }

        public static (string code, string playerId, bool ready, string leaderId) DecodeEvent(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString(), r.ReadBoolean(), r.ReadString());
        }
    }

    internal sealed class Party
    {
        public readonly string Code;
        public readonly List<Guid> Order = new List<Guid>();   // Order[0] == leader
        public readonly Dictionary<Guid, BasePeer> Members = new Dictionary<Guid, BasePeer>();
        public readonly Dictionary<Guid, bool> Ready = new Dictionary<Guid, bool>();
        public Party(string code) => Code = code;
        public Guid Leader => Order.Count > 0 ? Order[0] : Guid.Empty;
    }

    /// <summary>Server-side party state: parties by 6-char code, leader = creator, auto-removal on disconnect.</summary>
    public sealed class PartyServer
    {
        private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
        private readonly ConcurrentDictionary<string, Party> _parties = new ConcurrentDictionary<string, Party>();
        private readonly ConcurrentDictionary<Guid, string> _memberParty = new ConcurrentDictionary<Guid, string>();
        private readonly object _gate = new object();

        internal PartyServer(BaseServer server) => server.PeerDisconnected += peer => Leave(peer);

        internal async Task HandleAsync(ChannelRequest req)
        {
            var peer = req.Peer;
            switch ((PartyOp)req.Op)
            {
                case PartyOp.Create:
                {
                    Leave(peer);
                    var party = new Party(NewCode());
                    lock (_gate) { _parties[party.Code] = party; Add(party, peer); }
                    await req.ReplyRawAsync(Snapshot(party, peer)).ConfigureAwait(false);
                    break;
                }
                case PartyOp.Join:
                {
                    var code = PartyWire.DecodeJoin(req.RawBody);
                    if (!_parties.TryGetValue(code ?? "", out var party)) throw new ProtocolException("party not found");
                    Leave(peer);
                    lock (_gate) Add(party, peer);
                    Notify(party, peer, PartyEvt.Joined, PartyWire.EncodeEvent(party.Code, PlayerId(peer), false, PlayerId2(party.Leader)));
                    await req.ReplyRawAsync(Snapshot(party, peer)).ConfigureAwait(false);
                    break;
                }
                case PartyOp.Leave:
                    Leave(peer);
                    if (req.ExpectsReply)
                        await req.ReplyRawAsync(PartyWire.EncodeReply("", PlayerId(peer), PlayerId(peer), Array.Empty<PartyMember>())).ConfigureAwait(false);
                    break;
                case PartyOp.SetReady:
                {
                    var ready = PartyWire.DecodeReady(req.RawBody);
                    if (_memberParty.TryGetValue(peer.CurrentPeerInfo.Id, out var code) && _parties.TryGetValue(code, out var party))
                    {
                        lock (_gate) party.Ready[peer.CurrentPeerInfo.Id] = ready;
                        Notify(party, null, PartyEvt.ReadyChanged, PartyWire.EncodeEvent(party.Code, PlayerId(peer), ready, PlayerId2(party.Leader)));
                        await req.ReplyRawAsync(Snapshot(party, peer)).ConfigureAwait(false);
                        break;
                    }
                    throw new ProtocolException("not in a party");
                }
            }
        }

        private void Add(Party party, BasePeer peer)
        {
            var id = peer.CurrentPeerInfo.Id;
            party.Order.Add(id); party.Members[id] = peer; party.Ready[id] = false;
            _memberParty[id] = party.Code;
        }

        private void Leave(BasePeer peer)
        {
            var id = peer.CurrentPeerInfo.Id;
            if (!_memberParty.TryRemove(id, out var code) || !_parties.TryGetValue(code, out var party)) return;
            bool leaderChanged; Guid newLeader;
            lock (_gate)
            {
                var wasLeader = party.Leader == id;
                party.Order.Remove(id); party.Members.Remove(id); party.Ready.Remove(id);
                leaderChanged = wasLeader && party.Order.Count > 0;
                newLeader = party.Leader;
                if (party.Order.Count == 0) { _parties.TryRemove(party.Code, out _); return; }
            }
            Notify(party, peer, PartyEvt.Left, PartyWire.EncodeEvent(party.Code, PlayerId(peer), false, PlayerId2(newLeader)));
            if (leaderChanged)
                Notify(party, null, PartyEvt.LeaderChanged, PartyWire.EncodeEvent(party.Code, PlayerId2(newLeader), false, PlayerId2(newLeader)));
        }

        private void Notify(Party party, BasePeer? except, PartyEvt evt, byte[] body)
        {
            List<BasePeer> targets;
            lock (_gate) targets = new List<BasePeer>(party.Members.Values);
            foreach (var m in targets)
            {
                if (except != null && m.CurrentPeerInfo.Id == except.CurrentPeerInfo.Id) continue;
                try { _ = m.PublishRawAsync(Channels.Party, (ushort)evt, body); } catch { /* dropping */ }
            }
        }

        private byte[] Snapshot(Party party, BasePeer self)
        {
            List<PartyMember> members; string leader;
            lock (_gate)
            {
                members = new List<PartyMember>(party.Order.Count);
                foreach (var id in party.Order) members.Add(new PartyMember(id.ToString("N"), party.Ready.TryGetValue(id, out var r) && r));
                leader = PlayerId2(party.Leader);
            }
            return PartyWire.EncodeReply(party.Code, PlayerId(self), leader, members);
        }

        /// <summary>The live member peers of the party with the given code (empty if unknown). Used by the <see cref="IPeerGroups"/> view.</summary>
        internal IReadOnlyList<BasePeer> MembersOf(string code)
        {
            if (code != null && _parties.TryGetValue(code, out var party))
                lock (_gate) return new List<BasePeer>(party.Members.Values);
            return Array.Empty<BasePeer>();
        }

        /// <summary>The party code this peer is in, or null.</summary>
        internal string? GroupKeyOf(BasePeer peer)
            => peer != null && _memberParty.TryGetValue(peer.CurrentPeerInfo.Id, out var code) ? code : null;

        private static string PlayerId(BasePeer peer) => peer.CurrentPeerInfo.Id.ToString("N");
        private static string PlayerId2(Guid id) => id == Guid.Empty ? "" : id.ToString("N");

        private string NewCode()
        {
            while (true)
            {
                var bytes = Guid.NewGuid().ToByteArray();
                var chars = new char[6];
                for (var i = 0; i < 6; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
                var code = new string(chars);
                if (!_parties.ContainsKey(code)) return code;
            }
        }
    }

    /// <summary>Client-side party driver.</summary>
    public sealed class PartyClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private string? _code;

        /// <summary>The current party, or null.</summary>
        public string? CurrentCode { get { lock (_gate) return _code; } }

        /// <summary>Raised when a member joins (arg: player id).</summary>
        public event Action<string>? PlayerJoined;
        /// <summary>Raised when a member leaves (arg: player id).</summary>
        public event Action<string>? PlayerLeft;
        /// <summary>Raised when the leader changes (arg: new leader player id).</summary>
        public event Action<string>? LeaderChanged;
        /// <summary>Raised when a member's ready state changes (args: player id, ready).</summary>
        public event Action<string, bool>? ReadyChanged;
        /// <summary>Raised when the party disbands.</summary>
        public event Action? Disbanded;

        internal PartyClient(BaseClient client)
        {
            _client = client;
            _subscriptions.Add(_client.OnRaw(Channels.Party, (ushort)PartyEvt.Joined, b => OnEvent(PartyEvt.Joined, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Party, (ushort)PartyEvt.Left, b => OnEvent(PartyEvt.Left, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Party, (ushort)PartyEvt.LeaderChanged, b => OnEvent(PartyEvt.LeaderChanged, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Party, (ushort)PartyEvt.ReadyChanged, b => OnEvent(PartyEvt.ReadyChanged, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Party, (ushort)PartyEvt.Disbanded, b => OnEvent(PartyEvt.Disbanded, b)));
        }

        /// <summary>Creates a party and returns it (you become the leader).</summary>
        public async Task<PartyInfo> CreateAsync() => Apply(await Request(PartyOp.Create, Array.Empty<byte>()).ConfigureAwait(false));
        /// <summary>Joins a party by code.</summary>
        public async Task<PartyInfo> JoinAsync(string code) => Apply(await Request(PartyOp.Join, PartyWire.EncodeJoin(code)).ConfigureAwait(false));
        /// <summary>Leaves the current party. Tolerant of a dropped connection (the server auto-removes a disconnected member).</summary>
        public async Task LeaveAsync()
        {
            try { await Request(PartyOp.Leave, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { /* already disconnected — server auto-removes us */ }
            lock (_gate) _code = null;
        }
        /// <summary>Sets this client's ready state.</summary>
        public async Task<PartyInfo> SetReadyAsync(bool ready) => Apply(await Request(PartyOp.SetReady, PartyWire.EncodeReady(ready)).ConfigureAwait(false));

        private PartyInfo Apply(byte[] replyBody)
        {
            var info = PartyWire.DecodeReply(replyBody);
            lock (_gate) _code = string.IsNullOrEmpty(info.Code) ? null : info.Code;
            return info;
        }

        private async Task<byte[]> Request(PartyOp op, byte[] body)
        {
            try { return await _client.RequestRawAsync(Channels.Party, (ushort)op, body).ConfigureAwait(false); }
            catch (ProtocolException ex) { throw new PartyException(ex.Message); }
            catch (TimeoutException) { throw new PartyException("Party command timed out."); }
        }

        private void OnEvent(PartyEvt type, byte[] body)
        {
            var (code, playerId, ready, _) = PartyWire.DecodeEvent(body);
            lock (_gate) { if (_code == null || _code != code) return; }
            switch (type)
            {
                case PartyEvt.Joined: PlayerJoined?.Invoke(playerId); break;
                case PartyEvt.Left: PlayerLeft?.Invoke(playerId); break;
                case PartyEvt.LeaderChanged: LeaderChanged?.Invoke(playerId); break;
                case PartyEvt.ReadyChanged: ReadyChanged?.Invoke(playerId, ready); break;
                case PartyEvt.Disbanded: lock (_gate) _code = null; Disbanded?.Invoke(); break;
            }
        }
    }

    internal static class PartyRegistry
    {
        private static readonly ConcurrentDictionary<BaseServer, PartyServer> Servers = new ConcurrentDictionary<BaseServer, PartyServer>();
        public static void RegisterServer(BaseServer s, PartyServer p) => Servers[s] = p;
        public static PartyServer? GetServer(BaseServer? s) => s != null && Servers.TryGetValue(s, out var p) ? p : null;
    }

    /// <summary><see cref="IPeerGroups"/> view over a server's parties — the same reusable primitive Rooms uses.</summary>
    internal sealed class PartyGroupsView : IPeerGroups
    {
        private readonly PartyServer _server;
        public PartyGroupsView(PartyServer server) => _server = server;
        public IReadOnlyList<BasePeer> MembersOf(string groupKey) => _server.MembersOf(groupKey);
        public string? GroupKeyOf(BasePeer peer) => _server.GroupKeyOf(peer);
    }

    /// <summary>Attaches parties by composition — no base class.</summary>
    public static class PartyExtensions
    {
        /// <summary>Enables the party server.</summary>
        public static PartyServer UseParties(this BaseServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var p = new PartyServer(server); PartyRegistry.RegisterServer(server, p); return p;
        }

        /// <summary>Enables the party client and returns the driver.</summary>
        public static PartyClient UseParty(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new PartyClient(client);
        }

        /// <summary>The parties grouping as an <see cref="IPeerGroups"/> (generic query/broadcast extensions apply); null if parties aren't enabled.</summary>
        public static IPeerGroups? PartyGroups(this BaseServer server)
        {
            var p = PartyRegistry.GetServer(server);
            return p == null ? null : new PartyGroupsView(p);
        }

        /// <summary>Every other member of the peer's party (all except the peer itself).</summary>
        public static IReadOnlyList<BasePeer> OthersInPartyOf(this BaseServer server, BasePeer peer)
            => server.PartyGroups() is { } g ? g.OthersOf(peer) : Array.Empty<BasePeer>();

        /// <summary>Pushes an event to the peer's party — everyone else by default, or including the peer when <paramref name="includeSelf"/> is true.</summary>
        public static Task BroadcastToPartyOfAsync<T>(this BaseServer server, BasePeer peer, ushort channel, ushort op, T message, bool includeSelf = false)
            => server.PartyGroups() is { } g ? g.BroadcastToGroupOfAsync(peer, channel, op, message, includeSelf) : Task.CompletedTask;
    }

    /// <summary>Auto-discovered channel service for party commands.</summary>
    [ProtocolChannel(Channels.Party)]
    public sealed class PartyChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var server = PartyRegistry.GetServer(request.Peer.CurrentPeerInfo.Server);
            if (server == null) throw new ProtocolException("parties are not configured on this server");
            return server.HandleAsync(request);
        }
    }

    /// <summary>One-time bootstrap so the party channel service is discovered. Call at startup.</summary>
    public static class PartyRuntime
    {
        /// <summary>Ensures the party layer is discoverable.</summary>
        public static void Enable() { _ = typeof(PartyChannelService); }
    }
}
