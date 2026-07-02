using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;

namespace SetNet.Guilds
{
    /// <summary>Command operations (client → server) within the Guilds protocol channel.</summary>
    internal enum GuildOp : ushort { Create = 1, Join = 2, Leave = 3, Promote = 4, Kick = 5, ListMembers = 6, BankDeposit = 7, BankWithdraw = 8, BankList = 9 }

    /// <summary>Push events (server → client) within the Guilds protocol channel.</summary>
    internal enum GuildEvt : ushort { MemberJoined = 10, MemberLeft = 11, Disbanded = 12 }

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

    /// <summary>Decoded guild command body (the op and correlation live in the protocol envelope).</summary>
    internal sealed class GuildCommand
    {
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
            w.Write(GuildId ?? ""); w.Write(Name ?? "");
            w.Write(TargetKey ?? ""); w.Write((byte)Role); w.Write(ItemId ?? ""); w.Write(Count);
            return ms.ToArray();
        }

        public static GuildCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new GuildCommand
            {
                GuildId = r.ReadString(), Name = r.ReadString(),
                TargetKey = r.ReadString(), Role = (GuildRole)r.ReadByte(), ItemId = r.ReadString(), Count = r.ReadInt64(),
            };
        }
    }

    /// <summary>Decoded guild reply body (payload only; op/correlation are in the envelope).</summary>
    internal sealed class GuildReply
    {
        public string GuildId = "";
        public List<GuildMember> Members = new List<GuildMember>();
        public List<ItemStack> Bank = new List<ItemStack>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(GuildId ?? "");
            w.Write(Members.Count);
            foreach (var m in Members) { w.Write(m.PlayerKey ?? ""); w.Write((byte)m.Role); }
            w.Write(Bank.Count);
            foreach (var b in Bank) { w.Write(b.ItemId ?? ""); w.Write(b.Count); }
            return ms.ToArray();
        }

        public static GuildReply Decode(byte[] data)
        {
            var reply = new GuildReply();
            if (data == null || data.Length == 0) return reply;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            reply.GuildId = r.ReadString();
            var mc = r.ReadInt32();
            for (var i = 0; i < mc; i++) reply.Members.Add(new GuildMember(r.ReadString(), (GuildRole)r.ReadByte()));
            var bc = r.ReadInt32();
            for (var i = 0; i < bc; i++) reply.Bank.Add(new ItemStack(r.ReadString(), r.ReadInt64()));
            return reply;
        }
    }

    /// <summary>A guild push event: a membership change the client reacts to (internal wire body).</summary>
    internal sealed class GuildEvent
    {
        public GuildEvt Type;
        public string GuildId = "";
        public string PlayerKey = "";

        internal byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(GuildId ?? ""); w.Write(PlayerKey ?? "");
            return ms.ToArray();
        }

        internal static GuildEvent Decode(GuildEvt type, byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new GuildEvent { Type = type, GuildId = r.ReadString(), PlayerKey = r.ReadString() };
        }
    }

    /// <summary>
    /// Client-side guild driver, attached by <see cref="GuildClientExtensions.UseGuilds"/>. Create or join a guild,
    /// manage members and roles, and use the shared guild bank (which is a guild-keyed inventory in the same
    /// <c>SetNet.Inventory</c> hub). Membership changes arrive via <see cref="MemberJoined"/>/<see cref="MemberLeft"/>/<see cref="Disbanded"/>.
    /// Rides the unified protocol on the <see cref="Channels.Guilds"/> channel.
    /// </summary>
    public sealed class GuildClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised when a member joins the guild you're in (arg: their player key).</summary>
        public event Action<string>? MemberJoined;

        /// <summary>Raised when a member leaves/is kicked (arg: their player key).</summary>
        public event Action<string>? MemberLeft;

        /// <summary>Raised when the guild is disbanded.</summary>
        public event Action? Disbanded;

        internal GuildClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Guilds, (ushort)GuildEvt.MemberJoined, b => OnEvent(GuildEvt.MemberJoined, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Guilds, (ushort)GuildEvt.MemberLeft, b => OnEvent(GuildEvt.MemberLeft, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Guilds, (ushort)GuildEvt.Disbanded, b => OnEvent(GuildEvt.Disbanded, b)));
        }

        /// <summary>Creates a guild and becomes its leader; returns the new guild id.</summary>
        public async Task<string> CreateAsync(string name)
        {
            var reply = await Send(GuildOp.Create, new GuildCommand { Name = name }).ConfigureAwait(false);
            return reply.GuildId;
        }

        /// <summary>Joins an existing guild by id.</summary>
        public Task JoinAsync(string guildId) => Send(GuildOp.Join, new GuildCommand { GuildId = guildId });

        /// <summary>Leaves your current guild (leader leaving transfers leadership or disbands the last member's guild).</summary>
        public Task LeaveAsync() => Send(GuildOp.Leave, new GuildCommand());

        /// <summary>Sets a member's role (leader only; setting Leader transfers leadership).</summary>
        public Task PromoteAsync(string memberKey, GuildRole role) => Send(GuildOp.Promote, new GuildCommand { TargetKey = memberKey, Role = role });

        /// <summary>Kicks a lower-ranked member (officer/leader only).</summary>
        public Task KickAsync(string memberKey) => Send(GuildOp.Kick, new GuildCommand { TargetKey = memberKey });

        /// <summary>Lists the guild's members and roles.</summary>
        public async Task<IReadOnlyList<GuildMember>> ListMembersAsync()
            => (await Send(GuildOp.ListMembers, new GuildCommand()).ConfigureAwait(false)).Members;

        /// <summary>Deposits items from your inventory into the guild bank.</summary>
        public Task BankDepositAsync(string itemId, long count) => Send(GuildOp.BankDeposit, new GuildCommand { ItemId = itemId, Count = count });

        /// <summary>Withdraws items from the guild bank into your inventory (officer/leader only).</summary>
        public Task BankWithdrawAsync(string itemId, long count) => Send(GuildOp.BankWithdraw, new GuildCommand { ItemId = itemId, Count = count });

        /// <summary>Lists the guild bank's contents.</summary>
        public async Task<IReadOnlyList<ItemStack>> BankListAsync()
            => (await Send(GuildOp.BankList, new GuildCommand()).ConfigureAwait(false)).Bank;

        private async Task<GuildReply> Send(GuildOp op, GuildCommand cmd)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Guilds, (ushort)op, cmd.Encode()).ConfigureAwait(false);
                return GuildReply.Decode(body);
            }
            catch (ProtocolException ex) { throw new GuildException(ex.Message); }
            catch (TimeoutException) { throw new GuildException("Guild command timed out."); }
        }

        private void OnEvent(GuildEvt type, byte[] body)
        {
            var evt = GuildEvent.Decode(type, body);
            switch (type)
            {
                case GuildEvt.MemberJoined: MemberJoined?.Invoke(evt.PlayerKey); break;
                case GuildEvt.MemberLeft: MemberLeft?.Invoke(evt.PlayerKey); break;
                case GuildEvt.Disbanded: Disbanded?.Invoke(); break;
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

        internal Task HandleAsync(ChannelRequest request)
        {
            var me = _inventory.KeyOf(request.Peer);
            var cmd = GuildCommand.Decode(request.RawBody);
            switch ((GuildOp)request.Op)
            {
                case GuildOp.Create: return Create(request, me, cmd);
                case GuildOp.Join: return Join(request, me, cmd);
                case GuildOp.Leave: return Leave(request, me, cmd);
                case GuildOp.Promote: return Promote(request, me, cmd);
                case GuildOp.Kick: return Kick(request, me, cmd);
                case GuildOp.ListMembers: return ListMembers(request, me, cmd);
                case GuildOp.BankDeposit: return Bank(request, me, cmd, withdraw: false);
                case GuildOp.BankWithdraw: return Bank(request, me, cmd, withdraw: true);
                case GuildOp.BankList: return BankList(request, me, cmd);
                default: return Task.CompletedTask;
            }
        }

        private async Task Create(ChannelRequest request, string me, GuildCommand cmd)
        {
            if (await _store.GetByMemberAsync(me).ConfigureAwait(false) != null) throw new ProtocolException("Already in a guild.");
            if (string.IsNullOrWhiteSpace(cmd.Name)) throw new ProtocolException("Guild name required.");
            var id = await _store.ReserveIdAsync().ConfigureAwait(false);
            var guild = new Guild { Id = id, Name = cmd.Name.Trim() };
            guild.Members[me] = GuildRole.Leader;
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = id }.Encode()).ConfigureAwait(false);
        }

        private async Task Join(ChannelRequest request, string me, GuildCommand cmd)
        {
            if (await _store.GetByMemberAsync(me).ConfigureAwait(false) != null) throw new ProtocolException("Already in a guild.");
            var guild = await _store.GetAsync(cmd.GuildId).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("No such guild.");
            lock (guild) guild.Members[me] = GuildRole.Member;
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id }.Encode()).ConfigureAwait(false);
            await NotifyMembers(guild, new GuildEvent { Type = GuildEvt.MemberJoined, GuildId = guild.Id, PlayerKey = me }, except: null).ConfigureAwait(false);
        }

        private async Task Leave(ChannelRequest request, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            await RemoveMember(guild, me).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task Promote(ChannelRequest request, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            lock (guild)
            {
                if (!guild.Members.TryGetValue(me, out var myRole) || myRole != GuildRole.Leader) throw new ProtocolException("Only the leader can change roles.");
                if (!guild.Members.ContainsKey(cmd.TargetKey ?? "")) throw new ProtocolException("Target is not a member.");
                if (cmd.Role == GuildRole.Leader) { guild.Members[cmd.TargetKey!] = GuildRole.Leader; guild.Members[me] = GuildRole.Officer; }   // transfer
                else guild.Members[cmd.TargetKey!] = cmd.Role;
            }
            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task Kick(ChannelRequest request, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            lock (guild)
            {
                if (!guild.Members.TryGetValue(me, out var myRole) || myRole < GuildRole.Officer) throw new ProtocolException("Only officers and the leader can kick.");
                if (!guild.Members.TryGetValue(cmd.TargetKey ?? "", out var targetRole)) throw new ProtocolException("Target is not a member.");
                if (targetRole >= myRole) throw new ProtocolException("Cannot kick an equal or higher rank.");
            }
            await RemoveMember(guild, cmd.TargetKey!).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task ListMembers(ChannelRequest request, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            List<GuildMember> members;
            lock (guild) members = guild.Members.Select(kv => new GuildMember(kv.Key, kv.Value)).ToList();
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id, Members = members }.Encode()).ConfigureAwait(false);
        }

        private async Task Bank(ChannelRequest request, string me, GuildCommand cmd, bool withdraw)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Count <= 0) throw new ProtocolException("Invalid item or count.");

            GuildRole role; lock (guild) guild.Members.TryGetValue(me, out role);
            if (withdraw && role < GuildRole.Officer) throw new ProtocolException("Only officers and the leader can withdraw.");

            var (fromKey, toKey) = withdraw ? (guild.BankKey, me) : (me, guild.BankKey);
            if (!await _inventory.TryRevokeAsync(fromKey, cmd.ItemId, cmd.Count).ConfigureAwait(false))
                throw new ProtocolException(withdraw ? "Guild bank lacks that item." : "You lack that item.");
            await _inventory.GrantAsync(toKey, cmd.ItemId, cmd.Count).ConfigureAwait(false);
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task BankList(ChannelRequest request, string me, GuildCommand cmd)
        {
            var guild = await _store.GetByMemberAsync(me).ConfigureAwait(false);
            if (guild == null) throw new ProtocolException("Not in a guild.");
            var bank = new List<ItemStack>(await _inventory.GetAsync(guild.BankKey).ConfigureAwait(false));
            await request.ReplyRawAsync(new GuildReply { GuildId = guild.Id, Bank = bank }.Encode()).ConfigureAwait(false);
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
                await NotifyMember(memberKey, new GuildEvent { Type = GuildEvt.Disbanded, GuildId = guild.Id }).ConfigureAwait(false);
                return;
            }

            await _store.UpsertAsync(guild).ConfigureAwait(false);
            await NotifyMembers(guild, new GuildEvent { Type = GuildEvt.MemberLeft, GuildId = guild.Id, PlayerKey = memberKey }, except: null).ConfigureAwait(false);
            await NotifyMember(memberKey, new GuildEvent { Type = GuildEvt.MemberLeft, GuildId = guild.Id, PlayerKey = memberKey }).ConfigureAwait(false);   // tell the leaver too
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
            try { return peer.PublishRawAsync(Channels.Guilds, (ushort)evt.Type, evt.Encode()); } catch { return Task.CompletedTask; }
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for guild commands.</summary>
    [ProtocolChannel(Channels.Guilds)]
    public sealed class GuildChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = GuildServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("guilds is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

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

    /// <summary>One-time bootstrap so the guild channel service is discovered. Call at startup.</summary>
    public static class GuildRuntime
    {
        /// <summary>Ensures the guild layer is discoverable.</summary>
        public static void Enable() { _ = typeof(GuildChannelService); }
    }
}
