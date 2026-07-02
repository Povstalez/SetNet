using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>
    /// A mutable, server-authoritative table of accumulated aggro: player key → threat value. The brain reads it to
    /// pick a target (highest threat), and the framework adds to it when a player damages the mob. Not thread-safe on
    /// its own — it is only touched from the mob tick / combat path, which the <see cref="MobServer"/> serializes.
    /// </summary>
    public sealed class ThreatTable
    {
        private readonly Dictionary<string, float> _threat = new Dictionary<string, float>();

        /// <summary>Adds <paramref name="amount"/> threat for a player (creating the entry if new).</summary>
        public void Add(string playerKey, float amount)
        {
            if (string.IsNullOrEmpty(playerKey)) return;
            _threat.TryGetValue(playerKey, out var have);
            _threat[playerKey] = have + amount;
        }

        /// <summary>Current threat for a player (0 if none).</summary>
        public float Of(string playerKey)
            => playerKey != null && _threat.TryGetValue(playerKey, out var v) ? v : 0f;

        /// <summary>The player key with the most threat, or null when the table is empty.</summary>
        public string? Highest()
        {
            string? best = null;
            var bestVal = float.NegativeInfinity;
            foreach (var kv in _threat)
                if (kv.Value > bestVal) { bestVal = kv.Value; best = kv.Key; }
            return best;
        }

        /// <summary>Removes a player from the table (e.g. they died, left, or dropped aggro).</summary>
        public void Remove(string playerKey) { if (playerKey != null) _threat.Remove(playerKey); }

        /// <summary>Clears all threat (e.g. on leash reset).</summary>
        public void Clear() => _threat.Clear();

        /// <summary>The number of players currently on the table.</summary>
        public int Count => _threat.Count;

        /// <summary>A snapshot of the current entries.</summary>
        public IReadOnlyDictionary<string, float> Entries => _threat;
    }

    /// <summary>An in-progress ability cast, telegraphed to observers so players can interrupt or dodge.</summary>
    public sealed class MobCastState
    {
        /// <summary>The ability being cast.</summary>
        public string AbilityId { get; set; } = "";

        /// <summary>The target player key this cast resolves against (empty for self/AoE-at-self casts).</summary>
        public string TargetKey { get; set; } = "";

        /// <summary>Total cast time in milliseconds.</summary>
        public int TotalMs { get; set; }

        /// <summary>Milliseconds remaining before the cast completes and the ability resolves.</summary>
        public int RemainingMs { get; set; }
    }

    /// <summary>
    /// One hostile AI entity's full server-side state. Position/health/target/cast are replicated to nearby players
    /// (via the replication seam); threat and the blackboard are server-only brain state. Positions use
    /// <see cref="SetNet.GeoData.Vec3"/> so movement can flow through <c>SetNet.PathFinding</c>/<c>SetNet.GeoData</c>.
    /// </summary>
    public sealed class MobInstance
    {
        /// <summary>Unique, server-assigned mob id.</summary>
        public string Id { get; internal set; } = "";

        /// <summary>The mob type key — selects which <see cref="IMobBrain"/> drives it.</summary>
        public string Type { get; internal set; } = "";

        /// <summary>Current world position.</summary>
        public Vec3 Position { get; set; }

        /// <summary>Current velocity (set by the movement layer each tick; for dead-reckoning on clients).</summary>
        public Vec3 Velocity { get; set; }

        /// <summary>The spawn point, used for leash checks and (optionally) respawn.</summary>
        public Vec3 SpawnPoint { get; internal set; }

        /// <summary>The zone/world-node this mob belongs to.</summary>
        public string Zone { get; internal set; } = "";

        /// <summary>Current health.</summary>
        public double Health { get; set; }

        /// <summary>Maximum health.</summary>
        public double MaxHealth { get; set; }

        /// <summary>The faction this mob belongs to; mobs ignore same-faction entities. Players are a faction too.</summary>
        public string Faction { get; internal set; } = "";

        /// <summary>Accumulated aggro per player.</summary>
        public ThreatTable Threat { get; } = new ThreatTable();

        /// <summary>The current target player key, or null.</summary>
        public string? Target { get; set; }

        /// <summary>The in-progress cast, or null when not casting.</summary>
        public MobCastState? Casting { get; set; }

        /// <summary>Free-form brain scratch space (patrol point, last-known target position, boss phase, …).</summary>
        public IDictionary<string, object> Blackboard { get; } = new Dictionary<string, object>();

        /// <summary>True while the mob still has positive health.</summary>
        public bool IsAlive => Health > 0;

        /// <summary>Health as a fraction of max (0..1), guarding a zero max.</summary>
        public double HealthFraction => MaxHealth > 0 ? Math.Max(0, Math.Min(1, Health / MaxHealth)) : 0;
    }

    /// <summary>A description of one damage event delivered to a mob (fed into <see cref="IMobBrain.OnDamagedAsync"/>).</summary>
    public sealed class DamageEvent
    {
        /// <summary>The player key that dealt the damage (the attacker).</summary>
        public string Source { get; }

        /// <summary>How much health was removed.</summary>
        public double Amount { get; }

        /// <summary>The ability id that dealt the damage (if known).</summary>
        public string? AbilityId { get; }

        /// <summary>Creates a damage event.</summary>
        public DamageEvent(string source, double amount, string? abilityId = null)
        {
            Source = source ?? "";
            Amount = amount;
            AbilityId = abilityId;
        }
    }

    /// <summary>
    /// A mob ability the framework enforces range, cooldown and cast time for. On cast completion it computes the
    /// affected targets and raises <c>MobAttack</c>, applying <see cref="Damage"/> through the app's damage sink.
    /// </summary>
    public sealed class MobAbility
    {
        /// <summary>The ability id (referenced by <c>MobContext.UseAbilityAsync</c> and the client attack command).</summary>
        public string Id { get; set; } = "";

        /// <summary>Maximum distance to the target for the ability to be usable.</summary>
        public float Range { get; set; }

        /// <summary>Cooldown in milliseconds between uses.</summary>
        public int CooldownMs { get; set; }

        /// <summary>Cast time in milliseconds (0 = instant). Telegraphed to observers while casting.</summary>
        public int CastTimeMs { get; set; }

        /// <summary>Damage applied to each affected target on resolution.</summary>
        public double Damage { get; set; }

        /// <summary>Optional status-effect id applied on hit (resolved by the app / SetNet.StatusEffects).</summary>
        public string? EffectId { get; set; }

        /// <summary>Area-of-effect radius; 0 means single-target (the resolved target only).</summary>
        public float AoeRadius { get; set; }

        /// <summary>Creates an ability.</summary>
        public MobAbility() { }

        /// <summary>Creates an ability with the common fields.</summary>
        public MobAbility(string id, float range, int cooldownMs, double damage, int castTimeMs = 0, float aoeRadius = 0, string? effectId = null)
        {
            Id = id; Range = range; CooldownMs = cooldownMs; Damage = damage;
            CastTimeMs = castTimeMs; AoeRadius = aoeRadius; EffectId = effectId;
        }
    }

    /// <summary>The parameters used to spawn a new mob.</summary>
    public sealed class MobSpawn
    {
        /// <summary>The mob type key (must match a registered brain).</summary>
        public string Type { get; set; } = "";

        /// <summary>Where to spawn (also the leash/respawn anchor).</summary>
        public Vec3 Position { get; set; }

        /// <summary>The zone/world-node.</summary>
        public string Zone { get; set; } = "";

        /// <summary>Starting/max health.</summary>
        public double Health { get; set; } = 100;

        /// <summary>The faction (mobs ignore same-faction). Defaults to "hostile".</summary>
        public string Faction { get; set; } = "hostile";

        /// <summary>Milliseconds after death before an automatic respawn at the spawn point; 0 = no respawn.</summary>
        public int RespawnMs { get; set; }

        /// <summary>Creates an empty spawn (for object-initializer syntax).</summary>
        public MobSpawn() { }
    }
}
