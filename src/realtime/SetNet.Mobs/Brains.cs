using System.Threading.Tasks;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>
    /// Aggros on sight: picks the highest-threat (else nearest) sensed player inside its aggro radius, closes to melee
    /// range, and attacks. Drops the target and heads home to regen when it strays past its leash radius. The
    /// canonical "attacks anything it sees" mob. Perception (aggro radius, LOS) is exposed so the framework builds
    /// senses to match.
    /// </summary>
    public sealed class AggressiveBrain : MobBrain
    {
        private readonly string _mobType;

        /// <summary>Radius within which players are sensed and aggroed.</summary>
        public float AggroRadius { get; }

        /// <summary>Whether line of sight is required to sense/aggro a player.</summary>
        public bool RequireLos { get; }

        /// <summary>Range at which the mob can use its melee ability.</summary>
        public float AttackRange { get; }

        /// <summary>Distance from spawn past which the mob drops its target and resets.</summary>
        public float LeashRadius { get; }

        /// <summary>The ability id used in melee (default "melee").</summary>
        public string MeleeAbility { get; }

        /// <summary>Creates an aggressive brain.</summary>
        public AggressiveBrain(string mobType, float aggroRadius = 12f, float attackRange = 2f, float leashRadius = 25f,
            bool requireLos = true, string meleeAbility = "melee")
        {
            _mobType = mobType;
            AggroRadius = aggroRadius; AttackRange = attackRange; LeashRadius = leashRadius;
            RequireLos = requireLos; MeleeAbility = meleeAbility;
        }

        /// <inheritdoc/>
        public override string MobType => _mobType;

        /// <inheritdoc/>
        public override async Task ThinkAsync(MobContext ctx, MobSenses senses)
        {
            var mob = ctx.Mob;
            if (!senses.InLeashRange)
            {
                if (mob.Target != null) ctx.SetTarget(null);
                ctx.MoveTo(mob.SpawnPoint);   // reset toward spawn (regen is app-owned)
                return;
            }

            var target = senses.Target;
            if (target == null)
            {
                target = MobTargeting.Pick(senses, TargetSelector.HighestThreat);
                if (target == null) return;
                ctx.SetTarget(target.PlayerKey);
            }

            ctx.Face(target.Position);
            if (target.Distance > AttackRange) ctx.MoveTo(target.Position);
            else await ctx.UseAbilityAsync(MeleeAbility, target.PlayerKey).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override Task OnTargetLostAsync(MobContext ctx) { ctx.SetTarget(null); return Task.CompletedTask; }
    }

    /// <summary>
    /// Peaceful until provoked: <see cref="ThinkAsync"/> idles (or holds its patrol point) while it has no target; the
    /// reactive <see cref="OnDamagedAsync"/> adds the attacker to the threat table and sets it as the target, after
    /// which it behaves aggressively (closes and attacks) until the target is lost or it leashes. This is the "only
    /// fights back when hit" mob and shows why the reactive hook exists separately from the tick.
    /// </summary>
    public sealed class PassiveRetaliateBrain : MobBrain
    {
        private readonly string _mobType;

        /// <summary>Radius within which players are sensed once provoked.</summary>
        public float AggroRadius { get; }

        /// <summary>Whether line of sight is required to sense a player.</summary>
        public bool RequireLos { get; }

        /// <summary>Melee range.</summary>
        public float AttackRange { get; }

        /// <summary>Leash radius from spawn.</summary>
        public float LeashRadius { get; }

        /// <summary>Melee ability id.</summary>
        public string MeleeAbility { get; }

        /// <summary>Creates a passive-retaliate brain.</summary>
        public PassiveRetaliateBrain(string mobType, float aggroRadius = 12f, float attackRange = 2f, float leashRadius = 25f,
            bool requireLos = true, string meleeAbility = "melee")
        {
            _mobType = mobType;
            AggroRadius = aggroRadius; AttackRange = attackRange; LeashRadius = leashRadius;
            RequireLos = requireLos; MeleeAbility = meleeAbility;
        }

        /// <inheritdoc/>
        public override string MobType => _mobType;

        /// <inheritdoc/>
        public override async Task ThinkAsync(MobContext ctx, MobSenses senses)
        {
            var mob = ctx.Mob;

            // No target → stay peaceful (return home if wandered).
            if (mob.Target == null)
            {
                if (!senses.InLeashRange) ctx.MoveTo(mob.SpawnPoint);
                return;
            }

            // Provoked: behave aggressively toward the attacker until lost/leashed.
            if (!senses.InLeashRange || senses.Target == null)
            {
                ctx.SetTarget(null);
                ctx.MoveTo(mob.SpawnPoint);
                return;
            }

            var target = senses.Target;
            ctx.Face(target.Position);
            if (target.Distance > AttackRange) ctx.MoveTo(target.Position);
            else await ctx.UseAbilityAsync(MeleeAbility, target.PlayerKey).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override Task OnDamagedAsync(MobContext ctx, DamageEvent dmg)
        {
            // The framework already added threat; latch on to the attacker (the whole point of this mob).
            if (!string.IsNullOrEmpty(dmg.Source)) ctx.SetTarget(dmg.Source);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task OnTargetLostAsync(MobContext ctx) { ctx.SetTarget(null); return Task.CompletedTask; }
    }

    /// <summary>
    /// Keeps the target in a distance band and shoots from range (kiting): backs away when the target is closer than
    /// <see cref="MinRange"/>, closes when it is farther than <see cref="MaxRange"/>, and fires when the target is
    /// inside the band with line of sight. Backs toward its spawn (allies) when cornered.
    /// </summary>
    public sealed class RangedBrain : MobBrain
    {
        private readonly string _mobType;

        /// <summary>Minimum stand-off distance (kite away when closer).</summary>
        public float MinRange { get; }

        /// <summary>Maximum engagement distance (close in when farther).</summary>
        public float MaxRange { get; }

        /// <summary>Sense radius.</summary>
        public float AggroRadius { get; }

        /// <summary>Whether LOS is required to sense a player.</summary>
        public bool RequireLos { get; }

        /// <summary>Leash radius from spawn.</summary>
        public float LeashRadius { get; }

        /// <summary>Ranged ability id (default "shoot").</summary>
        public string ShootAbility { get; }

        /// <summary>Creates a ranged/kiting brain.</summary>
        public RangedBrain(string mobType, float minRange = 6f, float maxRange = 18f, float aggroRadius = 20f,
            float leashRadius = 30f, bool requireLos = true, string shootAbility = "shoot")
        {
            _mobType = mobType;
            MinRange = minRange; MaxRange = maxRange; AggroRadius = aggroRadius;
            LeashRadius = leashRadius; RequireLos = requireLos; ShootAbility = shootAbility;
        }

        /// <inheritdoc/>
        public override string MobType => _mobType;

        /// <inheritdoc/>
        public override async Task ThinkAsync(MobContext ctx, MobSenses senses)
        {
            var mob = ctx.Mob;
            if (!senses.InLeashRange)
            {
                if (mob.Target != null) ctx.SetTarget(null);
                ctx.MoveTo(mob.SpawnPoint);
                return;
            }

            var target = senses.Target;
            if (target == null)
            {
                target = MobTargeting.Pick(senses, TargetSelector.HighestThreat);
                if (target == null) return;
                ctx.SetTarget(target.PlayerKey);
            }

            ctx.Face(target.Position);
            var dist = target.Distance;
            if (dist < MinRange)
            {
                // Kite: move directly away from the target (toward spawn/allies when it happens to line up).
                var away = mob.Position - target.Position;
                var dir = away.LengthSquared > 1e-6f ? away.Normalized : (mob.SpawnPoint - mob.Position).Normalized;
                if (dir.LengthSquared < 1e-6f) dir = new Vec3(1, 0, 0);
                ctx.MoveTo(mob.Position + dir * MinRange);
            }
            else if (dist > MaxRange)
            {
                ctx.MoveTo(target.Position);   // close in
            }
            else if (target.HasLineOfSight)
            {
                await ctx.UseAbilityAsync(ShootAbility, target.PlayerKey).ConfigureAwait(false);
            }
            else
            {
                ctx.MoveTo(target.Position);   // reposition to reacquire LOS
            }
        }

        /// <inheritdoc/>
        public override Task OnTargetLostAsync(MobContext ctx) { ctx.SetTarget(null); return Task.CompletedTask; }
    }

    /// <summary>
    /// A caster that picks an ability by situation each tick from a priority list guarded by predicates: self-heal when
    /// low, an AoE when enough players cluster, otherwise a single-target bolt. Casts are telegraphed (the framework
    /// starts a <see cref="MobCastState"/> with a cast time replicated to observers, so players can interrupt/dodge)
    /// and the framework tracks cooldowns and resolves effects on completion.
    /// </summary>
    public sealed class CasterBrain : MobBrain
    {
        private readonly string _mobType;

        /// <summary>Health fraction below which the caster prioritizes a self-heal.</summary>
        public double LowHealthFraction { get; }

        /// <summary>How many clustered players trigger the AoE ability.</summary>
        public int AoeThreshold { get; }

        /// <summary>The radius within which players count as "clustered" for the AoE decision.</summary>
        public float AoeClusterRadius { get; }

        /// <summary>Sense radius.</summary>
        public float AggroRadius { get; }

        /// <summary>Whether LOS is required.</summary>
        public bool RequireLos { get; }

        /// <summary>Leash radius from spawn.</summary>
        public float LeashRadius { get; }

        /// <summary>The maximum distance at which the caster will cast at its target (approaches otherwise).</summary>
        public float CastRange { get; }

        /// <summary>Self-heal ability id.</summary>
        public string HealAbility { get; }

        /// <summary>AoE ability id.</summary>
        public string AoeAbility { get; }

        /// <summary>Single-target ability id.</summary>
        public string BoltAbility { get; }

        /// <summary>Creates a caster brain.</summary>
        public CasterBrain(string mobType, double lowHealthFraction = 0.3, int aoeThreshold = 3, float aoeClusterRadius = 6f,
            float aggroRadius = 22f, float leashRadius = 30f, float castRange = 20f, bool requireLos = true,
            string healAbility = "heal", string aoeAbility = "firestorm", string boltAbility = "bolt")
        {
            _mobType = mobType;
            LowHealthFraction = lowHealthFraction; AoeThreshold = aoeThreshold; AoeClusterRadius = aoeClusterRadius;
            AggroRadius = aggroRadius; LeashRadius = leashRadius; CastRange = castRange; RequireLos = requireLos;
            HealAbility = healAbility; AoeAbility = aoeAbility; BoltAbility = boltAbility;
        }

        /// <inheritdoc/>
        public override string MobType => _mobType;

        /// <inheritdoc/>
        public override async Task ThinkAsync(MobContext ctx, MobSenses senses)
        {
            var mob = ctx.Mob;
            if (mob.Casting != null) return;   // already casting; let it finish

            if (!senses.InLeashRange)
            {
                if (mob.Target != null) ctx.SetTarget(null);
                ctx.MoveTo(mob.SpawnPoint);
                return;
            }

            // 1) Emergency self-heal.
            if (senses.HealthFraction < LowHealthFraction)
            {
                await ctx.UseAbilityAsync(HealAbility, mob.Id).ConfigureAwait(false);   // self target
                return;
            }

            var target = senses.Target;
            if (target == null)
            {
                target = MobTargeting.Pick(senses, TargetSelector.HighestThreat);
                if (target == null) return;
                ctx.SetTarget(target.PlayerKey);
            }

            ctx.Face(target.Position);

            // Close in if the target is out of casting range.
            if (target.Distance > CastRange) { ctx.MoveTo(target.Position); return; }

            // 2) AoE when enough players cluster near the target.
            var clustered = 0;
            foreach (var p in senses.Nearby)
                if (Vec3.Distance(p.Position, target.Position) <= AoeClusterRadius) clustered++;
            if (clustered >= AoeThreshold)
            {
                await ctx.UseAbilityAsync(AoeAbility, target.PlayerKey).ConfigureAwait(false);
                return;
            }

            // 3) Single-target bolt.
            await ctx.UseAbilityAsync(BoltAbility, target.PlayerKey).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override Task OnDamagedAsync(MobContext ctx, DamageEvent dmg)
        {
            if (ctx.Mob.Target == null && !string.IsNullOrEmpty(dmg.Source)) ctx.SetTarget(dmg.Source);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task OnTargetLostAsync(MobContext ctx) { ctx.SetTarget(null); return Task.CompletedTask; }
    }
}
