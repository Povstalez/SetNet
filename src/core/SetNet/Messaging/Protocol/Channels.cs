namespace SetNet.Protocol
{
    /// <summary>
    /// The central registry of channel ids for the unified protocol. Every companion module that speaks
    /// request/reply + push events over <see cref="ProtocolTypes.Envelope"/> owns one stable channel id here, so
    /// the ids are allocated in one place (no collisions) instead of each module reserving its own wire-type triple.
    /// </summary>
    /// <remarks>
    /// Ids are small and dense (they are namespaced by the single envelope wire type, not by the global 16-bit
    /// message-type space). Within a channel, the module assigns its own <c>Op</c> ids (typically a small enum) for
    /// its commands and events. Do not renumber an existing channel — that is a wire-breaking change.
    /// </remarks>
    public static class Channels
    {
        /// <summary>Rooms / lobbies (SetNet.Rooms).</summary>
        public const ushort Rooms = 1;

        /// <summary>Queue-based matchmaking (SetNet.Matchmaking).</summary>
        public const ushort Matchmaking = 2;

        /// <summary>Persistent social parties (SetNet.Party).</summary>
        public const ushort Party = 3;

        /// <summary>Text chat (SetNet.Chat).</summary>
        public const ushort Chat = 4;

        /// <summary>Server-authoritative inventory (SetNet.Inventory).</summary>
        public const ushort Inventory = 5;

        /// <summary>Player-to-player escrow trading (SetNet.Trade).</summary>
        public const ushort Trade = 6;

        /// <summary>Offline mail with attachments (SetNet.Mail).</summary>
        public const ushort Mail = 7;

        /// <summary>Seamless zone handoff (SetNet.Zones).</summary>
        public const ushort Zones = 8;

        /// <summary>Currency wallet (SetNet.Wallet).</summary>
        public const ushort Wallet = 9;

        /// <summary>TURN-style opaque relay (SetNet.Relay).</summary>
        public const ushort Relay = 10;

        /// <summary>NAT hole-punch coordination (SetNet.NatPunch).</summary>
        public const ushort NatPunch = 11;

        /// <summary>Consistent-hash shard directory (SetNet.Sharding).</summary>
        public const ushort Sharding = 12;

        /// <summary>Load-balancer directory (SetNet.LoadBalancer).</summary>
        public const ushort LoadBalancer = 13;

        /// <summary>Crafting (SetNet.Crafting).</summary>
        public const ushort Crafting = 14;

        /// <summary>Loot tables (SetNet.Loot).</summary>
        public const ushort Loot = 15;

        /// <summary>Player progression / XP (SetNet.Progression).</summary>
        public const ushort Progression = 16;

        /// <summary>Quests (SetNet.Quests).</summary>
        public const ushort Quests = 17;

        /// <summary>Guilds (SetNet.Guilds).</summary>
        public const ushort Guilds = 18;

        /// <summary>Auction house (SetNet.Auction).</summary>
        public const ushort Auction = 19;

        /// <summary>NPC vendors (SetNet.Vendor).</summary>
        public const ushort Vendor = 20;

        /// <summary>Player marketplace (SetNet.Marketplace).</summary>
        public const ushort Marketplace = 21;

        /// <summary>Status effects / buffs (SetNet.StatusEffects).</summary>
        public const ushort StatusEffects = 22;

        /// <summary>Host migration (SetNet.Rooms.HostMigration).</summary>
        public const ushort HostMigration = 23;

        /// <summary>Deterministic lockstep input (SetNet.Lockstep).</summary>
        public const ushort Lockstep = 24;

        /// <summary>RPC request/reply (SetNet.Rpc) — the op is the RPC method id.</summary>
        public const ushort Rpc = 25;

        /// <summary>Interactive non-living entities (SetNet.NPC).</summary>
        public const ushort Npc = 26;

        /// <summary>Hostile AI entities (SetNet.Mobs).</summary>
        public const ushort Mobs = 27;

        /// <summary>Player abilities / skills (SetNet.Abilities).</summary>
        public const ushort Abilities = 28;

        /// <summary>Equipment slots (SetNet.Equipment).</summary>
        public const ushort Equipment = 29;

        /// <summary>Server→client notifications (SetNet.Notifications).</summary>
        public const ushort Notifications = 30;

        /// <summary>Branching dialogue (SetNet.Dialogue).</summary>
        public const ushort Dialogue = 31;

        /// <summary>Login coordinator: auth + server list + session-token handoff (SetNet.LoginServer).</summary>
        public const ushort Login = 32;

        /// <summary>Turn-based board/card game hub — reserved for the future networked layer (SetNet.BoardGame).</summary>
        public const ushort BoardGame = 33;
    }
}
