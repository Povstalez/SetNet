using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>Thrown when a mob operation fails (attack rejected, command timeout).</summary>
    public sealed class MobException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public MobException(string message) : base(message) { }
    }

    /// <summary>A mob that became visible to the client (from a <c>MobSpawned</c> event).</summary>
    public sealed class MobSpawnedInfo
    {
        /// <summary>The mob id.</summary>
        public string Id { get; }
        /// <summary>The mob type key.</summary>
        public string Type { get; }
        /// <summary>Spawn position.</summary>
        public Vec3 Position { get; }
        /// <summary>Current health at spawn.</summary>
        public double Health { get; }
        /// <summary>Maximum health.</summary>
        public double MaxHealth { get; }
        /// <summary>The zone.</summary>
        public string Zone { get; }
        /// <summary>The faction.</summary>
        public string Faction { get; }

        /// <summary>Creates the info.</summary>
        public MobSpawnedInfo(string id, string type, Vec3 position, double health, double maxHealth, string zone, string faction)
        {
            Id = id; Type = type; Position = position; Health = health; MaxHealth = maxHealth; Zone = zone; Faction = faction;
        }
    }

    /// <summary>A resolved mob ability (from a <c>MobAttack</c> event) — for playing VFX the raw state doesn't convey.</summary>
    public sealed class MobAttackInfo
    {
        /// <summary>The attacking mob's id.</summary>
        public string MobId { get; }
        /// <summary>The ability id.</summary>
        public string AbilityId { get; }
        /// <summary>Damage dealt to each target.</summary>
        public double Damage { get; }
        /// <summary>The affected player keys.</summary>
        public IReadOnlyList<string> Targets { get; }

        /// <summary>Creates the info.</summary>
        public MobAttackInfo(string mobId, string abilityId, double damage, IReadOnlyList<string> targets)
        {
            MobId = mobId; AbilityId = abilityId; Damage = damage; Targets = targets;
        }
    }

    /// <summary>A mob acquired a target (from a <c>MobAggro</c> event).</summary>
    public sealed class MobAggroInfo
    {
        /// <summary>The mob's id.</summary>
        public string MobId { get; }
        /// <summary>The newly targeted player key.</summary>
        public string TargetKey { get; }

        /// <summary>Creates the info.</summary>
        public MobAggroInfo(string mobId, string targetKey) { MobId = mobId; TargetKey = targetKey; }
    }

    /// <summary>A mob died (from a <c>MobDeath</c> event).</summary>
    public sealed class MobDeathInfo
    {
        /// <summary>The dead mob's id.</summary>
        public string MobId { get; }
        /// <summary>The killer's player key, or null for an environmental death.</summary>
        public string? KillerKey { get; }
        /// <summary>Where the mob died (for loot/corpse VFX).</summary>
        public Vec3 Position { get; }

        /// <summary>Creates the info.</summary>
        public MobDeathInfo(string mobId, string? killerKey, Vec3 position) { MobId = mobId; KillerKey = killerKey; Position = position; }
    }
}
