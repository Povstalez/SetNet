using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>
    /// One player as perceived by a mob this tick: who they are, how far, whether the mob can see them, and their
    /// current threat toward the mob.
    /// </summary>
    public sealed class PerceivedPlayer
    {
        /// <summary>The player's stable key.</summary>
        public string PlayerKey { get; }

        /// <summary>Distance from the mob to the player.</summary>
        public float Distance { get; }

        /// <summary>Whether the mob has an unobstructed line of sight to the player.</summary>
        public bool HasLineOfSight { get; }

        /// <summary>The player's accumulated threat toward this mob.</summary>
        public float Threat { get; }

        /// <summary>The player's world position (as known to the perception seam).</summary>
        public Vec3 Position { get; }

        /// <summary>Creates a perceived-player record.</summary>
        public PerceivedPlayer(string playerKey, float distance, bool hasLineOfSight, float threat, Vec3 position)
        {
            PlayerKey = playerKey; Distance = distance; HasLineOfSight = hasLineOfSight; Threat = threat; Position = position;
        }
    }

    /// <summary>
    /// A perception snapshot the framework builds for a mob each tick: the players it can sense, the resolved current
    /// target (if still valid), whether it is still within leash range of its spawn, and its health fraction. The brain
    /// reads only this — it never queries the world directly.
    /// </summary>
    public sealed class MobSenses
    {
        /// <summary>All players the mob can currently sense (within aggro radius, LOS-filtered when required).</summary>
        public IReadOnlyList<PerceivedPlayer> Nearby { get; }

        /// <summary>The mob's resolved current target if it is still sensed, else null.</summary>
        public PerceivedPlayer? Target { get; }

        /// <summary>True while the mob is still within its leash radius of its spawn point.</summary>
        public bool InLeashRange { get; }

        /// <summary>The mob's current health as a fraction of max (0..1).</summary>
        public double HealthFraction { get; }

        /// <summary>Creates a senses snapshot.</summary>
        public MobSenses(IReadOnlyList<PerceivedPlayer> nearby, PerceivedPlayer? target, bool inLeashRange, double healthFraction)
        {
            Nearby = nearby; Target = target; InLeashRange = inLeashRange; HealthFraction = healthFraction;
        }
    }

    /// <summary>
    /// The brain's action surface for one mob. The brain never mutates the world directly — it *intends* actions and
    /// the framework resolves them so movement stays authoritative and abilities respect cooldowns/cast times.
    /// </summary>
    public interface MobContext
    {
        /// <summary>The mob this context drives (read its state; intents below decide what happens to it).</summary>
        MobInstance Mob { get; }

        /// <summary>Intends to move toward <paramref name="pos"/> (the movement layer paths/steers there next tick).</summary>
        void MoveTo(Vec3 pos);

        /// <summary>Intends to face <paramref name="pos"/> (orientation only; no motion).</summary>
        void Face(Vec3 pos);

        /// <summary>Sets (or clears, with null) the mob's current target player key.</summary>
        void SetTarget(string? playerKey);

        /// <summary>Starts casting an ability at a target if it is off cooldown and in range (no-op otherwise).</summary>
        Task UseAbilityAsync(string abilityId, string targetKey);

        /// <summary>Broadcasts a flavour emote to nearby observers.</summary>
        void Say(string emote);

        /// <summary>App services (damage sink, loot/xp sinks, status effects, …) for brains that need them.</summary>
        IServiceProvider Services { get; }
    }

    /// <summary>
    /// The per-mob-type AI. Exactly one brain instance is registered per <see cref="MobType"/>; the framework calls it
    /// for every mob of that type. <see cref="ThinkAsync"/> runs on the tick; the reactive hooks fire off the combat
    /// path. Implement it directly, subclass <see cref="MobBrain"/> for no-op defaults, or build one with
    /// <see cref="MobBrain.Compose"/>.
    /// </summary>
    public interface IMobBrain
    {
        /// <summary>The mob type key this brain drives.</summary>
        string MobType { get; }

        /// <summary>Called once when a mob of this type spawns (seed the blackboard, pick a patrol point, …).</summary>
        void OnSpawn(MobContext ctx);

        /// <summary>Called every AI tick with a fresh perception snapshot. Issue movement/target/ability intents here.</summary>
        Task ThinkAsync(MobContext ctx, MobSenses senses);

        /// <summary>Called reactively when the mob takes damage (off the tick) — add threat, retaliate, enter combat, …</summary>
        Task OnDamagedAsync(MobContext ctx, DamageEvent dmg);

        /// <summary>Called when the current target is lost (died, left, or fled past leash).</summary>
        Task OnTargetLostAsync(MobContext ctx);

        /// <summary>Called when the mob dies (killer key may be null for environmental deaths).</summary>
        Task OnDeathAsync(MobContext ctx, string? killerKey);
    }

    /// <summary>
    /// A convenience base class implementing <see cref="IMobBrain"/> with no-op defaults so a brain overrides only the
    /// hooks it needs. Also the home of the <see cref="Compose"/> fluent builder that turns config into a brain.
    /// </summary>
    public abstract class MobBrain : IMobBrain
    {
        /// <inheritdoc/>
        public abstract string MobType { get; }

        /// <inheritdoc/>
        public virtual void OnSpawn(MobContext ctx) { }

        /// <inheritdoc/>
        public virtual Task ThinkAsync(MobContext ctx, MobSenses senses) => Task.CompletedTask;

        /// <inheritdoc/>
        public virtual Task OnDamagedAsync(MobContext ctx, DamageEvent dmg) => Task.CompletedTask;

        /// <inheritdoc/>
        public virtual Task OnTargetLostAsync(MobContext ctx) => Task.CompletedTask;

        /// <inheritdoc/>
        public virtual Task OnDeathAsync(MobContext ctx, string? killerKey) => Task.CompletedTask;

        /// <summary>Starts composing a brain for <paramref name="mobType"/> from reusable behaviour components.</summary>
        public static MobBrainBuilder Compose(string mobType) => new MobBrainBuilder(mobType);
    }
}
