using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;

namespace SetNet.Guilds
{
    /// <summary>Reserved wire types for the guild service. Don't reuse these ids for application messages.</summary>
    public static class GuildTypes
    {
        /// <summary>Client → server: guild command.</summary>
        public const ushort Command = ushort.MaxValue - 77;   // 65458

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 78;     // 65457

        /// <summary>Server → client: push event (member joined/left, disbanded).</summary>
        public const ushort Event = ushort.MaxValue - 79;     // 65456
    }

    internal enum GuildOp : byte { Create = 0, Join = 1, Leave = 2, Promote = 3, Kick = 4, ListMembers = 5, BankDeposit = 6, BankWithdraw = 7, BankList = 8 }
    internal enum GuildEventType : byte { MemberJoined = 0, MemberLeft = 1, Disbanded = 2 }

    /// <summary>A member's rank within a guild.</summary>
    public enum GuildRole : byte
    {
        /// <summary>Ordinary member: can deposit to the bank, not withdraw.</summary>
        Member = 0,

        /// <summary>Officer: can withdraw from the bank and kick members.</summary>
        Officer = 1,

        /// <summary>Leader: full control (promote, kick, disband, transfer).</summary>
        Leader = 2,
    }

    /// <summary>Thrown when a guild operation fails (permission, membership, timeout).</summary>
    public sealed class GuildException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public GuildException(string message) : base(message) { }
    }

    /// <summary>A guild member: player key + role.</summary>
    public sealed class GuildMember
    {
        /// <summary>The member's player key.</summary>
        public string PlayerKey { get; set; } = "";

        /// <summary>The member's role.</summary>
        public GuildRole Role { get; set; }

        /// <summary>Creates an empty member (for serialization).</summary>
        public GuildMember() { }

        /// <summary>Creates a member entry.</summary>
        public GuildMember(string playerKey, GuildRole role) { PlayerKey = playerKey; Role = role; }
    }

    /// <summary>A guild: id, name, and its members with roles.</summary>
    public sealed class Guild
    {
        /// <summary>Unique guild id.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Members keyed by player key.</summary>
        public Dictionary<string, GuildRole> Members { get; set; } = new Dictionary<string, GuildRole>();

        /// <summary>The bank inventory key this guild's shared stash is stored under (in the inventory hub).</summary>
        public string BankKey => "guild:" + Id;
    }

    // ---- store ----

    /// <summary>Persistence for guilds. Default <see cref="MemoryGuildStore"/> (in-process); swap for Redis/DB.</summary>
    public interface IGuildStore
    {
        /// <summary>Returns a guild by id, or null.</summary>
        Task<Guild?> GetAsync(string guildId);

        /// <summary>Returns the guild a player belongs to, or null.</summary>
        Task<Guild?> GetByMemberAsync(string playerKey);

        /// <summary>Reserves a unique guild id (used by create).</summary>
        Task<string> ReserveIdAsync();

        /// <summary>Creates or updates a guild.</summary>
        Task UpsertAsync(Guild guild);

        /// <summary>Removes a guild (disband); false when absent.</summary>
        Task<bool> RemoveAsync(string guildId);
    }

    /// <summary>In-process guild store.</summary>
    public sealed class MemoryGuildStore : IGuildStore
    {
        private readonly ConcurrentDictionary<string, Guild> _guilds = new ConcurrentDictionary<string, Guild>();

        /// <inheritdoc/>
        public Task<Guild?> GetAsync(string guildId) => Task.FromResult(_guilds.TryGetValue(guildId ?? "", out var g) ? g : null);

        /// <inheritdoc/>
        public Task<Guild?> GetByMemberAsync(string playerKey)
        {
            foreach (var g in _guilds.Values) lock (g) if (g.Members.ContainsKey(playerKey ?? "")) return Task.FromResult<Guild?>(g);
            return Task.FromResult<Guild?>(null);
        }

        /// <inheritdoc/>
        public Task<string> ReserveIdAsync() => Task.FromResult(Guid.NewGuid().ToString("N").Substring(0, 12));

        /// <inheritdoc/>
        public Task UpsertAsync(Guild guild) { _guilds[guild.Id] = guild; return Task.CompletedTask; }

        /// <inheritdoc/>
        public Task<bool> RemoveAsync(string guildId) => Task.FromResult(_guilds.TryRemove(guildId ?? "", out _));
    }

    // ---- wire ----

    internal sealed class GuildCommand
    {
        public int CorrelationId;
        public GuildOp Op;
        public string GuildId = "";
        public string Name = "";
        public string TargetKey = "";
        public GuildRole Role;
        public string ItemId = "";
        public long Count;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write((byte)Op); w.Write(GuildId ?? ""); w.Write(Name ?? "");
            w.Write(TargetKey ?? ""); w.Write((byte)Role); w.Write(ItemId ?? ""); w.Write(Count);
            return ms.ToArray();
        }

        public static GuildCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new GuildCommand
            {
                CorrelationId = r.ReadInt32(), Op = (GuildOp)r.ReadByte(), GuildId = r.ReadString(), Name = r.ReadString(),
                TargetKey = r.ReadString(), Role = (GuildRole)r.ReadByte(), ItemId = r.ReadString(), Count = r.ReadInt64(),
            };
        }
    }

    internal sealed class GuildReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string GuildId = "";
        public List<GuildMember> Members = new List<GuildMember>();
        public List<ItemStack> Bank = new List<ItemStack>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write(Success); w.Write(Error ?? ""); w.Write(GuildId ?? "");
            w.Write(Members.Count);
            foreach (var m in Members) { w.Write(m.PlayerKey ?? ""); w.Write((byte)m.Role); }
            w.Write(Bank.Count);
            foreach (var b in Bank) { w.Write(b.ItemId ?? ""); w.Write(b.Count); }
            return ms.ToArray();
        }

        public static GuildReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new GuildReply { CorrelationId = r.ReadInt32(), Success = r.ReadBoolean(), Error = r.ReadString(), GuildId = r.ReadString() };
            var mc = r.ReadInt32();
            for (var i = 0; i < mc; i++) reply.Members.Add(new GuildMember(r.ReadString(), (GuildRole)r.ReadByte()));
            var bc = r.ReadInt32();
            for (var i = 0; i < bc; i++) reply.Bank.Add(new ItemStack(r.ReadString(), r.ReadInt64()));
            return reply;
        }
    }

    /// <summary>A guild push event: a membership change the client reacts to (internal wire type).</summary>
    internal sealed class GuildEvent
    {
        public GuildEventType Type;
        public string GuildId = "";
        public string PlayerKey = "";

        internal byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)Type); w.Write(GuildId ?? ""); w.Write(PlayerKey ?? "");
            return ms.ToArray();
        }

        internal static GuildEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new GuildEvent { Type = (GuildEventType)r.ReadByte(), GuildId = r.ReadString(), PlayerKey = r.ReadString() };
        }
    }

    internal static class GuildRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<GuildReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<GuildReply>>();
        private static readonly ConcurrentDictionary<GuildClient, byte> Clients = new ConcurrentDictionary<GuildClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<GuildReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, GuildReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(GuildClient c) => Clients[c] = 0;
        public static void DispatchEvent(GuildEvent evt) { foreach (var c in Clients.Keys) c.OnEvent(evt); }
    }

    /// <summary>
    /// Client-side guild driver, attached by <see cref="GuildClientExtensions.UseGuilds"/>. Create or join a guild,
    /// manage members and roles, and use the shared guild bank (which is a guild-keyed inventory in the same
    /// <c>SetNet.Inventory</c> hub). Membership changes arrive via <see cref="MemberJoined"/>/<see cref="MemberLeft"/>/<see cref="Disbanded"/>.
    /// </summary>
    public sealed class GuildClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when a member joins the guild you're in (arg: their player key).</summary>
        public event Action<string>? MemberJoined;

        /// <summary>Raised when a member leaves/is kicked (arg: their player key).</summary>
        public event Action<string>? MemberLeft;

        /// <summary>Raised when the guild is disbanded.</summary>
        public event Action? Disbanded;

        internal GuildClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            GuildRegistry.RegisterClient(this);
        }

        /// <summary>Creates a guild and becomes its leader; returns the new guild id.</summary>
        public async Task<string> CreateAsync(string name)
        {
            var reply = await Send(new GuildCommand { Op = GuildOp.Create, Name = name }).ConfigureAwait(false);
            return reply.GuildId;
        }

        /// <summary>Joins an existing guild by id.</summary>
        public Task JoinAsync(string guildId) => Send(new GuildCommand { Op = GuildOp.Join, GuildId = guildId });

        /// <summary>Leaves your current guild (leader leaving transfers leadership or disbands the last member's guild).</summary>
        public Task LeaveAsync() => Send(new GuildCommand { Op = GuildOp.Leave });

        /// <summary>Sets a member's role (leader only; setting Leader transfers leadership).</summary>
        public Task PromoteAsync(string memberKey, GuildRole role) => Send(new GuildCommand { Op = GuildOp.Promote, TargetKey = memberKey, Role = role });

        /// <summary>Kicks a lower-ranked member (officer/leader only).</summary>
        public Task KickAsync(string memberKey) => Send(new GuildCommand { Op = GuildOp.Kick, TargetKey = memberKey });

        /// <summary>Lists the guild's members and roles.</summary>
        public async Task<IReadOnlyList<GuildMember>> ListMembersAsync()
            => (await Send(new GuildCommand { Op = GuildOp.ListMembers }).ConfigureAwait(false)).Members;

        /// <summary>Deposits items from your inventory into the guild bank.</summary>
        public Task BankDepositAsync(string itemId, long count) => Send(new GuildCommand { Op = GuildOp.BankDeposit, ItemId = itemId, Count = count });

        /// <summary>Withdraws items from the guild bank into your inventory (officer/leader only).</summary>
        public Task BankWithdrawAsync(string itemId, long count) => Send(new GuildCommand { Op = GuildOp.BankWithdraw, ItemId = itemId, Count = count });

        /// <summary>Lists the guild bank's contents.</summary>
        public async Task<IReadOnlyList<ItemStack>> BankListAsync()
            => (await Send(new GuildCommand { Op = GuildOp.BankList }).ConfigureAwait(false)).Bank;

        private async Task<GuildReply> Send(GuildCommand cmd)
        {
            var id = GuildRegistry.NextId();
            cmd.CorrelationId = id;
            var tcs = new TaskCompletionSource<GuildReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            GuildRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(GuildTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    GuildReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new GuildException("Guild command timed out."); }
                    if (!reply.Success) throw new GuildException(reply.Error);
                    return reply;
                }
            }
            finally { GuildRegistry.Remove(id); }
        }

        internal void OnEvent(GuildEvent evt)
        {
            switch (evt.Type)
            {
                case GuildEventType.MemberJoined: MemberJoined?.Invoke(evt.PlayerKey); break;
                case GuildEventType.MemberLeft: MemberLeft?.Invoke(evt.PlayerKey); break;
                case GuildEventType.Disbanded: Disbanded?.Invoke(); break;
            }
        }
    }

    /// <summary>
    /// Server-side guild hub, attached by <see cref="GuildServerExtensions.UseGuilds"/>. Owns guild membership and
    /// roles, and runs a **shared guild bank** as a guild-keyed inventory in the same <see cref="InventoryServer"/>
    /// (bank key <c>guild:&lt;id&gt;</c>) so deposits/withdrawals are atomic item moves with the same anti-dupe
    /// guarantees. Role rules: anyone deposits, officers/leaders withdraw and kick, leaders promote and disband.
    /// </summary>
    public sealed class GuildServer
    {
        private static readonly ConcurrentDictionary<BaseServer, GuildServer> Servers = new ConcurrentDictionary<BaseServer, GuildServer>();

        private readonly InventoryServer _inventory;
        private readonly IGuildStore _store;

        internal GuildServer(InventoryServer inventory, IGuildStore store) { _inventory = inventory; _store = store; }

        internal static GuildServer Enable(BaseServer server, InventoryServer inventory, IGuildStore? store)
            => Servers.GetOrAdd(server, _ => new GuildServer(inventory, store ?? new MemoryGuildStore()));

        internal static GuildServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        internal async Task OnCommand(BasePeer peer, GuildCommand cmd)
        {
            var me = _inventory.KeyOf(peer);
            try
            {
                switch (cmd.Op)
                {
                    case GuildOp.Create: await Create(peer, me, cmd); break;
                    case GuildOp.Join: await Join(peer, me, cmd); break;
                    case GuildOp.Leave: await Leave(peer, me, cmd); break;
                    case GuildOp.Promote: await Promote(peer, me, cmd); break;
                    case GuildOp.Kick: await Kick(peer, me, cmd); break;
                    case GuildOp.ListMembers: await ListMembers(peer, me, cmd); break;
                    case GuildOp.BankDeposit: await Bank(peer, me, cmd, withdraw: false); break;
                    case GuildOp.BankWithdraw: await Bank(peer, me, cmd, withdraw: true); break;
                    case GuildOp.BankList: await BankList(peer, me, cmd); break;
                }
            }
            catch (Exception ex) { await Reply(peer, cmd.CorrelationId, false, ex.Message, ""); }
        }

        private async Task Create(BasePeer peer, string me, GuildCommand cmd)
        {
            if (await _store.GetByMemberAsync(me).ConfigureAwait(false) != null) { await Reply(peer, cmd.CorrelationId, false, "Already in a guild.", ""); return; }
            if (string.IsNullOrWhiteSpace(cmd.Name)) { await Reply(peer, cmd.CorrelationId, false, "Guild name required.", ""); return; }
            var id = await _store.ReserveIdAsync().ConfigureAwait(false);
            var guild = new Guild { Id = id, Name = cmd.Name.Trim() };
            guild.Members[me] = GuildRole.Leader;
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", id);
        }

        private async Task Join(BasePeer peer, string me, GuildCommand cmd)
        {
            if (await _store.GetByMemberAsync(me).ConfigureAwait(false) != null) { await Reply(peer, cmd.CorrelationId, false, "Already in a guild.", ""); return; }
            var guild = await _store.GetAsync(cmd.GuildId).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "No such guild.", ""); return; }
            lock (guild) guild.Members[me] = GuildRole.Member;
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id);
            await NotifyMembers(guild, new GuildEvent { Type = GuildEventType.MemberJoined, GuildId = guild.Id, PlayerKey = me }, except: null).ConfigureAwait(false);
        }

        private async Task Leave(BasePeer peer, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            await RemoveMember(guild, me).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id);
        }

        private async Task Promote(BasePeer peer, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            lock (guild)
            {
                if (!guild.Members.TryGetValue(me, out var myRole) || myRole != GuildRole.Leader) throw new GuildException("Only the leader can change roles.");
                if (!guild.Members.ContainsKey(cmd.TargetKey ?? "")) throw new GuildException("Target is not a member.");
                if (cmd.Role == GuildRole.Leader) { guild.Members[cmd.TargetKey!] = GuildRole.Leader; guild.Members[me] = GuildRole.Officer; }   // transfer
                else guild.Members[cmd.TargetKey!] = cmd.Role;
            }
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id);
        }

        private async Task Kick(BasePeer peer, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            lock (guild)
            {
                if (!guild.Members.TryGetValue(me, out var myRole) || myRole < GuildRole.Officer) throw new GuildException("Only officers and the leader can kick.");
                if (!guild.Members.TryGetValue(cmd.TargetKey ?? "", out var targetRole)) throw new GuildException("Target is not a member.");
                if (targetRole >= myRole) throw new GuildException("Cannot kick an equal or higher rank.");
            }
            await RemoveMember(guild, cmd.TargetKey!).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id);
        }

        private async Task ListMembers(BasePeer peer, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            List<GuildMember> members;
            lock (guild) members = guild.Members.Select(kv => new GuildMember(kv.Key, kv.Value)).ToList();
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id, members).ConfigureAwait(false);
        }

        private async Task Bank(BasePeer peer, string me, GuildCommand cmd, bool withdraw)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Count <= 0) { await Reply(peer, cmd.CorrelationId, false, "Invalid item or count.", ""); return; }

            GuildRole role; lock (guild) guild.Members.TryGetValue(me, out role);
            if (withdraw && role < GuildRole.Officer) { await Reply(peer, cmd.CorrelationId, false, "Only officers and the leader can withdraw.", ""); return; }

            var (fromKey, toKey) = withdraw ? (guild.BankKey, me) : (me, guild.BankKey);
            if (!await _inventory.TryRevokeAsync(fromKey, cmd.ItemId, cmd.Count).ConfigureAwait(false))
            { await Reply(peer, cmd.CorrelationId, false, withdraw ? "Guild bank lacks that item." : "You lack that item.", ""); return; }
            await _inventory.GrantAsync(toKey, cmd.ItemId, cmd.Count).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id);
        }

        private async Task BankList(BasePeer peer, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) { await Reply(peer, cmd.CorrelationId, false, "Not in a guild.", ""); return; }
            var bank = new List<ItemStack>(await _inventory.GetAsync(guild.BankKey).ConfigureAwait(false));
            await Reply(peer, cmd.CorrelationId, true, "", guild.Id, bank: bank).ConfigureAwait(false);
        }

        /// <summary>Removes a member; promotes a successor if the leader left, or disbands (returning the bank) on the last member.</summary>
        private async Task RemoveMember(Guild guild, string memberKey)
        {
            bool disband = false; string? successor = null; GuildRole leftRole;
            lock (guild)
            {
                if (!guild.Members.TryGetValue(memberKey, out leftRole)) return;
                guild.Members.Remove(memberKey);
                if (guild.Members.Count == 0) disband = true;
                else if (leftRole == GuildRole.Leader)
                {
                    // Promote the highest-ranked remaining member (officers first, then anyone).
                    successor = guild.Members.OrderByDescending(kv => (byte)kv.Value).First().Key;
                    guild.Members[successor] = GuildRole.Leader;
                }
            }

            if (disband)
            {
                // Return the bank to the departing (last) member so nothing is destroyed, then remove the guild.
                foreach (var stack in await _inventory.GetAsync(guild.BankKey).ConfigureAwait(false))
                    if (await _inventory.TryRevokeAsync(guild.BankKey, stack.ItemId, stack.Count).ConfigureAwait(false))
                        await _inventory.GrantAsync(memberKey, stack.ItemId, stack.Count).ConfigureAwait(false);
                await _store.RemoveAsync(guild.Id).ConfigureAwait(false);
                await NotifyMember(memberKey, new GuildEvent { Type = GuildEventType.Disbanded, GuildId = guild.Id }).ConfigureAwait(false);
                return;
            }

            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await NotifyMembers(guild, new GuildEvent { Type = GuildEventType.MemberLeft, GuildId = guild.Id, PlayerKey = memberKey }, except: null).ConfigureAwait(false);
            await NotifyMember(memberKey, new GuildEvent { Type = GuildEventType.MemberLeft, GuildId = guild.Id, PlayerKey = memberKey }).ConfigureAwait(false);   // tell the leaver too
        }

        private async Task NotifyMembers(Guild guild, GuildEvent evt, string? except)
        {
            List<string> members; lock (guild) members = new List<string>(guild.Members.Keys);
            foreach (var key in members) { if (key == except) continue; await NotifyMember(key, evt).ConfigureAwait(false); }
        }

        private Task NotifyMember(string playerKey, GuildEvent evt)
        {
            var peer = _inventory.PeerFor(playerKey);
            if (peer == null) return Task.CompletedTask;
            try { return peer.SendAsync(GuildTypes.Event, evt.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, string guildId, List<GuildMember>? members = null, List<ItemStack>? bank = null)
        {
            var reply = new GuildReply { CorrelationId = corr, Success = ok, Error = error, GuildId = guildId, Members = members ?? new List<GuildMember>(), Bank = bank ?? new List<ItemStack>() };
            try { return peer.SendAsync(GuildTypes.Reply, reply.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for guild commands.</summary>
    [MessageHandler(GuildTypes.Command)]
    public sealed class GuildCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = GuildServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, GuildCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated guild replies.</summary>
    [MessageHandler(GuildTypes.Reply)]
    public sealed class GuildReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = GuildReply.Decode(data); GuildRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for guild push events.</summary>
    [MessageHandler(GuildTypes.Event)]
    public sealed class GuildEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { GuildRegistry.DispatchEvent(GuildEvent.Decode(data)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the guild hub to a server by composition.</summary>
    public static class GuildServerExtensions
    {
        /// <summary>Enables server-side guilds. Pass the <see cref="InventoryServer"/> from <c>UseInventory</c> (backs the guild bank).</summary>
        public static GuildServer UseGuilds(this BaseServer server, InventoryServer inventory, IGuildStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            return GuildServer.Enable(server, inventory, store);
        }
    }

    /// <summary>Attaches a guild driver to a client by composition.</summary>
    public static class GuildClientExtensions
    {
        /// <summary>Enables client-side guilds; returns the driver (create/join/leave/promote/kick/bank + events).</summary>
        public static GuildClient UseGuilds(this BaseClient client) => new GuildClient(client);
    }

    /// <summary>One-time bootstrap so the guild handlers are discovered. Call at startup.</summary>
    public static class GuildRuntime
    {
        /// <summary>Ensures the guild layer is discoverable.</summary>
        public static void Enable() { _ = GuildTypes.Command; }
    }
}
