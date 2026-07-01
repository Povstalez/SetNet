using System;
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

namespace SetNet.Party
{
    /// <summary>Reserved wire types for the party protocol. Don't reuse.</summary>
    public static class PartyTypes
    {
        /// <summary>Server → client push event.</summary>
        public const ushort Event = ushort.MaxValue - 24;     // 65511
        /// <summary>Client → server command.</summary>
        public const ushort Command = ushort.MaxValue - 23;   // 65512
        /// <summary>Server → client reply (correlated).</summary>
        public const ushort Reply = ushort.MaxValue - 22;     // 65513
    }

    internal enum PartyOp : byte { Create = 0, Join = 1, Leave = 2, SetReady = 3 }
    internal enum PartyEventType : byte { Joined = 0, Left = 1, LeaderChanged = 2, ReadyChanged = 3, Disbanded = 4 }

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
    public class PartyException : Exception { /// <summary>Creates the exception.</summary>
        public PartyException(string message) : base(message) { } }

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

        internal PartyReply Handle(BasePeer peer, PartyCommand cmd)
        {
            switch (cmd.Op)
            {
                case PartyOp.Create:
                {
                    Leave(peer);
                    var party = new Party(NewCode());
                    lock (_gate) { _parties[party.Code] = party; Add(party, peer); }
                    return Snapshot(cmd.CorrelationId, party, peer);
                }
                case PartyOp.Join:
                {
                    if (!_parties.TryGetValue(cmd.Code ?? "", out var party)) return PartyReply.Fail(cmd.CorrelationId, "party not found");
                    Leave(peer);
                    lock (_gate) Add(party, peer);
                    Notify(party, peer, new PartyEvent(party.Code, PartyEventType.Joined, PlayerId(peer), false, PlayerId2(party.Leader)));
                    return Snapshot(cmd.CorrelationId, party, peer);
                }
                case PartyOp.Leave:
                    Leave(peer);
                    return PartyReply.Ok(cmd.CorrelationId, "", PlayerId(peer), PlayerId(peer), Array.Empty<PartyMember>());
                case PartyOp.SetReady:
                {
                    if (_memberParty.TryGetValue(peer.CurrentPeerInfo.Id, out var code) && _parties.TryGetValue(code, out var party))
                    {
                        lock (_gate) party.Ready[peer.CurrentPeerInfo.Id] = cmd.Ready;
                        Notify(party, null, new PartyEvent(party.Code, PartyEventType.ReadyChanged, PlayerId(peer), cmd.Ready, PlayerId2(party.Leader)));
                        return Snapshot(cmd.CorrelationId, party, peer);
                    }
                    return PartyReply.Fail(cmd.CorrelationId, "not in a party");
                }
            }
            return PartyReply.Fail(cmd.CorrelationId, "unknown op");
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
            Notify(party, peer, new PartyEvent(party.Code, PartyEventType.Left, PlayerId(peer), false, PlayerId2(newLeader)));
            if (leaderChanged)
                Notify(party, null, new PartyEvent(party.Code, PartyEventType.LeaderChanged, PlayerId2(newLeader), false, PlayerId2(newLeader)));
        }

        private void Notify(Party party, BasePeer? except, PartyEvent evt)
        {
            var bytes = evt.Encode();
            List<BasePeer> targets;
            lock (_gate) targets = new List<BasePeer>(party.Members.Values);
            foreach (var m in targets)
            {
                if (except != null && m.CurrentPeerInfo.Id == except.CurrentPeerInfo.Id) continue;
                try { _ = m.SendAsync(PartyTypes.Event, bytes, DeliveryMethod.Reliable); } catch { /* dropping */ }
            }
        }

        private PartyReply Snapshot(int corr, Party party, BasePeer self)
        {
            List<PartyMember> members; string leader;
            lock (_gate)
            {
                members = new List<PartyMember>(party.Order.Count);
                foreach (var id in party.Order) members.Add(new PartyMember(id.ToString("N"), party.Ready.TryGetValue(id, out var r) && r));
                leader = PlayerId2(party.Leader);
            }
            return PartyReply.Ok(corr, party.Code, PlayerId(self), leader, members);
        }

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

        internal PartyClient(BaseClient client) { _client = client; PartyRegistry.RegisterClient(this); }

        /// <summary>Creates a party and returns it (you become the leader).</summary>
        public async Task<PartyInfo> CreateAsync() => Apply(await Send(PartyOp.Create, "", false).ConfigureAwait(false));
        /// <summary>Joins a party by code.</summary>
        public async Task<PartyInfo> JoinAsync(string code) => Apply(await Send(PartyOp.Join, code, false).ConfigureAwait(false));
        /// <summary>Leaves the current party.</summary>
        public async Task LeaveAsync() { await Send(PartyOp.Leave, "", false).ConfigureAwait(false); lock (_gate) _code = null; }
        /// <summary>Sets this client's ready state.</summary>
        public async Task<PartyInfo> SetReadyAsync(bool ready) => Apply(await Send(PartyOp.SetReady, CurrentCode ?? "", ready).ConfigureAwait(false));

        private PartyInfo Apply(PartyReply reply)
        {
            if (!reply.Success) throw new PartyException(reply.Error);
            lock (_gate) _code = string.IsNullOrEmpty(reply.Code) ? null : reply.Code;
            return new PartyInfo(reply.Code, reply.OwnPlayerId, reply.LeaderId, reply.Members);
        }

        private async Task<PartyReply> Send(PartyOp op, string code, bool ready)
        {
            var corr = PartyRegistry.NextId();
            var tcs = new TaskCompletionSource<PartyReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            PartyRegistry.Register(corr, tcs);
            try
            {
                await _client.SendAsync(PartyTypes.Command, new PartyCommand(corr, op, code, ready).Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new PartyException("Party command timed out."); }
                }
            }
            finally { PartyRegistry.Remove(corr); }
        }

        internal void OnEvent(PartyEvent evt)
        {
            lock (_gate) { if (_code == null || _code != evt.Code) return; }
            switch (evt.Type)
            {
                case PartyEventType.Joined: PlayerJoined?.Invoke(evt.PlayerId); break;
                case PartyEventType.Left: PlayerLeft?.Invoke(evt.PlayerId); break;
                case PartyEventType.LeaderChanged: LeaderChanged?.Invoke(evt.PlayerId); break;
                case PartyEventType.ReadyChanged: ReadyChanged?.Invoke(evt.PlayerId, evt.Ready); break;
                case PartyEventType.Disbanded: lock (_gate) _code = null; Disbanded?.Invoke(); break;
            }
        }
    }

    internal readonly struct PartyCommand
    {
        public readonly int CorrelationId; public readonly PartyOp Op; public readonly string Code; public readonly bool Ready;
        public PartyCommand(int corr, PartyOp op, string code, bool ready) { CorrelationId = corr; Op = op; Code = code ?? ""; Ready = ready; }
        public byte[] Encode() { using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(CorrelationId); w.Write((byte)Op); w.Write(Code); w.Write(Ready); } return ms.ToArray(); }
        public static PartyCommand Decode(byte[] f) { using var ms = new MemoryStream(f); using var r = new BinaryReader(ms, Encoding.UTF8); return new PartyCommand(r.ReadInt32(), (PartyOp)r.ReadByte(), r.ReadString(), r.ReadBoolean()); }
    }

    internal readonly struct PartyReply
    {
        public readonly int CorrelationId; public readonly bool Success; public readonly string Code; public readonly string OwnPlayerId; public readonly string LeaderId; public readonly IReadOnlyList<PartyMember> Members; public readonly string Error;
        private PartyReply(int c, bool s, string code, string own, string leader, IReadOnlyList<PartyMember> m, string e) { CorrelationId = c; Success = s; Code = code ?? ""; OwnPlayerId = own ?? ""; LeaderId = leader ?? ""; Members = m ?? Array.Empty<PartyMember>(); Error = e ?? ""; }
        public static PartyReply Ok(int c, string code, string own, string leader, IReadOnlyList<PartyMember> m) => new PartyReply(c, true, code, own, leader, m, "");
        public static PartyReply Fail(int c, string e) => new PartyReply(c, false, "", "", "", Array.Empty<PartyMember>(), e);
        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId); w.Write(Success);
                if (Success) { w.Write(Code); w.Write(OwnPlayerId); w.Write(LeaderId); w.Write(Members.Count); foreach (var m in Members) { w.Write(m.PlayerId); w.Write(m.Ready); } }
                else w.Write(Error);
            }
            return ms.ToArray();
        }
        public static PartyReply Decode(byte[] f)
        {
            using var ms = new MemoryStream(f); using var r = new BinaryReader(ms, Encoding.UTF8);
            var c = r.ReadInt32(); var s = r.ReadBoolean();
            if (!s) return Fail(c, r.ReadString());
            var code = r.ReadString(); var own = r.ReadString(); var leader = r.ReadString(); var n = r.ReadInt32();
            var members = new List<PartyMember>(n);
            for (var i = 0; i < n; i++) members.Add(new PartyMember(r.ReadString(), r.ReadBoolean()));
            return Ok(c, code, own, leader, members);
        }
    }

    internal readonly struct PartyEvent
    {
        public readonly string Code; public readonly PartyEventType Type; public readonly string PlayerId; public readonly bool Ready; public readonly string LeaderId;
        public PartyEvent(string code, PartyEventType type, string playerId, bool ready, string leaderId) { Code = code ?? ""; Type = type; PlayerId = playerId ?? ""; Ready = ready; LeaderId = leaderId ?? ""; }
        public byte[] Encode() { using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(Code); w.Write((byte)Type); w.Write(PlayerId); w.Write(Ready); w.Write(LeaderId); } return ms.ToArray(); }
        public static PartyEvent Decode(byte[] f) { using var ms = new MemoryStream(f); using var r = new BinaryReader(ms, Encoding.UTF8); return new PartyEvent(r.ReadString(), (PartyEventType)r.ReadByte(), r.ReadString(), r.ReadBoolean(), r.ReadString()); }
    }

    internal static class PartyRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<PartyReply>> Pending = new ConcurrentDictionary<int, TaskCompletionSource<PartyReply>>();
        private static readonly ConcurrentDictionary<BaseServer, PartyServer> Servers = new ConcurrentDictionary<BaseServer, PartyServer>();
        private static readonly ConcurrentDictionary<PartyClient, byte> Clients = new ConcurrentDictionary<PartyClient, byte>();
        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int c, TaskCompletionSource<PartyReply> t) => Pending[c] = t;
        public static void Remove(int c) => Pending.TryRemove(c, out _);
        public static void Complete(int c, PartyReply r) { if (Pending.TryGetValue(c, out var t)) t.TrySetResult(r); }
        public static void RegisterServer(BaseServer s, PartyServer p) => Servers[s] = p;
        public static PartyServer? GetServer(BaseServer? s) => s != null && Servers.TryGetValue(s, out var p) ? p : null;
        public static void RegisterClient(PartyClient c) => Clients[c] = 0;
        public static void DispatchEvent(PartyEvent e) { foreach (var c in Clients.Keys) c.OnEvent(e); }
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
    }

    /// <summary>Auto-discovered server handler for party commands.</summary>
    [MessageHandler(PartyTypes.Command)]
    public sealed class PartyCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var cmd = PartyCommand.Decode(data);
            var server = PartyRegistry.GetServer(peer.CurrentPeerInfo.Server);
            var reply = server?.Handle(peer, cmd) ?? PartyReply.Fail(cmd.CorrelationId, "parties are not configured on this server");
            return peer.SendAsync(PartyTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
        }
    }

    /// <summary>Auto-discovered client handler for party replies.</summary>
    [MessageHandler(PartyTypes.Reply)]
    public sealed class PartyReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = PartyReply.Decode(data); PartyRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for party events.</summary>
    [MessageHandler(PartyTypes.Event)]
    public sealed class PartyEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { PartyRegistry.DispatchEvent(PartyEvent.Decode(data)); return Task.CompletedTask; }
    }

    /// <summary>One-time bootstrap so the party handlers are discovered. Call at startup.</summary>
    public static class PartyRuntime
    {
        /// <summary>Ensures the party layer is discoverable.</summary>
        public static void Enable() { _ = PartyTypes.Command; }
    }
}
