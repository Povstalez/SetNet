using System;
using System.Collections.Generic;
using SetNet.Core;
using SetNet.GeoData;
using SetNet.PathFinding;

namespace SetNet.Mobs
{
    /// <summary>
    /// Configuration for the server-side mob hub (<c>server.UseMobs</c>). Everything is a seam so the module stays
    /// engine- and dependency-agnostic: supply where players are, how movement is pathed, how state is replicated, and
    /// how damage is applied. Sensible defaults let it run standalone (straight-line movement, no-op replication,
    /// built-in HP).
    /// </summary>
    public sealed class MobOptions
    {
        /// <summary>
        /// The world geometry, used for line-of-sight in perception and to build a pathfinder when none is supplied.
        /// Null → no LOS filtering and straight-line movement.
        /// </summary>
        public IGeoData? GeoData { get; set; }

        /// <summary>
        /// The pathfinder mobs move with. When null and <see cref="GeoData"/> is a supported kind, one is built via
        /// <c>Pathfinding.For</c>; otherwise movement is straight-line toward the <c>MoveTo</c> intent.
        /// </summary>
        public IPathfinder? Pathfinder { get; set; }

        /// <summary>
        /// Supplies the current position of a player key (the app knows this — e.g. from its own StateSync). Mutually
        /// exclusive-ish with <see cref="Players"/>; if both are set, <see cref="Players"/> wins. Pairs with
        /// <see cref="AllPlayers"/> for the candidate set.
        /// </summary>
        public Func<string, Vec3?>? PlayerPosition { get; set; }

        /// <summary>Enumerates the player keys that could be perceived (usually all online players). Used with <see cref="PlayerPosition"/>.</summary>
        public Func<IEnumerable<string>>? AllPlayers { get; set; }

        /// <summary>A full player-positions provider; overrides <see cref="PlayerPosition"/>/<see cref="AllPlayers"/> when set.</summary>
        public IPlayerPositions? Players { get; set; }

        /// <summary>The perception implementation. Default <see cref="RadiusPerception"/>.</summary>
        public IMobPerception Perception { get; set; } = RadiusPerception.Instance;

        /// <summary>App services surfaced to brains via <c>MobContext.Services</c> (damage sink, loot/xp sinks, …).</summary>
        public IServiceProvider? Services { get; set; }

        /// <summary>The replication seam. Default <see cref="NullMobReplication"/> — plug an adapter (e.g. SetNet.Mobs.StateSync) to push state.</summary>
        public IMobReplication Replication { get; set; } = NullMobReplication.Instance;

        /// <summary>Maps a connected peer to the stable player key used in threat/targeting/damage. Default = connection id.</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>AI ticks per second. Default 10.</summary>
        public int TickRateHz { get; set; } = 10;

        /// <summary>Default movement speed (world units/second) when a brain doesn't override it. Default 4.</summary>
        public float MoveSpeed { get; set; } = 4f;

        /// <summary>
        /// When true (default) the hub runs its own <see cref="System.Threading.Timer"/> at <see cref="TickRateHz"/>.
        /// Set false to drive the tick yourself via <c>MobServer.Update(dtMs)</c> (e.g. from your game loop).
        /// </summary>
        public bool UseInternalTimer { get; set; } = true;

        /// <summary>Threat added per point of player-dealt damage. Default 1.</summary>
        public float ThreatPerDamage { get; set; } = 1f;

        /// <summary>The registered abilities, keyed by ability id, that the framework enforces range/cooldown/cast for.</summary>
        public IDictionary<string, MobAbility> Abilities { get; } = new Dictionary<string, MobAbility>();

        /// <summary>Registers an ability (fluent).</summary>
        public MobOptions AddAbility(MobAbility ability)
        {
            if (ability != null && !string.IsNullOrEmpty(ability.Id)) Abilities[ability.Id] = ability;
            return this;
        }
    }
}
