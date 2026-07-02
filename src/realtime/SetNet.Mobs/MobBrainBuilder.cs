using System;
using System.Threading.Tasks;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>How a composed brain picks its target from the sensed players.</summary>
    public enum TargetSelector
    {
        /// <summary>The player with the highest accumulated threat (falls back to nearest when no threat yet).</summary>
        HighestThreat = 0,
        /// <summary>The nearest sensed player.</summary>
        Nearest = 1,
    }

    /// <summary>Namespace for target-selection choices used by the fluent builder (<c>Threat.Highest</c>, <c>Threat.Nearest</c>).</summary>
    public static class Threat
    {
        /// <summary>Select the highest-threat player.</summary>
        public static TargetSelector Highest => TargetSelector.HighestThreat;
        /// <summary>Select the nearest player.</summary>
        public static TargetSelector Nearest => TargetSelector.Nearest;
    }

    /// <summary>How a composed brain positions itself relative to its target.</summary>
    public sealed class Positioning
    {
        /// <summary>Melee/approach mode: close to within <see cref="Range"/>.</summary>
        public bool IsKite { get; }
        /// <summary>For approach: the attack range to close to. For kite: the maximum band distance.</summary>
        public float Range { get; }
        /// <summary>For kite only: the minimum band distance to back away from.</summary>
        public float MinRange { get; }

        private Positioning(bool isKite, float range, float minRange) { IsKite = isKite; Range = range; MinRange = minRange; }

        /// <summary>Approach until within <paramref name="range"/> of the target, then hold.</summary>
        public static Positioning Melee(float range) => new Positioning(false, range, 0);

        /// <summary>Keep the target inside the [<paramref name="min"/>, <paramref name="max"/>] band (kite when too close, close when too far).</summary>
        public static Positioning Kite(float min, float max) => new Positioning(true, max, min);
    }

    /// <summary>The positioning-mode factory used by the fluent builder (<c>Approach.Melee(...)</c>, <c>Approach.Kite(...)</c>).</summary>
    public static class Approach
    {
        /// <summary>Melee approach to a range.</summary>
        public static Positioning Melee(float range) => Positioning.Melee(range);
        /// <summary>Kite within a distance band.</summary>
        public static Positioning Kite(float min, float max) => Positioning.Kite(min, max);
    }

    /// <summary>An action a composed brain performs when in position (currently: use an ability whenever off cooldown).</summary>
    public sealed class MobAction
    {
        /// <summary>The ability id to use on the target.</summary>
        public string AbilityId { get; }

        private MobAction(string abilityId) { AbilityId = abilityId; }

        /// <summary>Use <paramref name="abilityId"/> on the target whenever it is off cooldown and in range.</summary>
        public static MobAction OnCooldown(string abilityId) => new MobAction(abilityId);
    }

    /// <summary>The action factory used by the fluent builder (<c>Ability.OnCooldown("slash")</c>).</summary>
    public static class Ability
    {
        /// <summary>Use an ability whenever it is off cooldown.</summary>
        public static MobAction OnCooldown(string abilityId) => MobAction.OnCooldown(abilityId);
    }

    /// <summary>
    /// A fluent builder that assembles an <see cref="IMobBrain"/> from reusable behaviour components running in a
    /// fixed pipeline — <c>Perceive → SelectTarget → Position(Approach|Kite) → Act(UseAbility)</c> — so most mobs are
    /// config, not code. Anything the pipeline cannot express, drop to a hand-written <see cref="IMobBrain"/>.
    /// </summary>
    public sealed class MobBrainBuilder
    {
        private readonly string _mobType;
        private float _aggroRadius = 12f;
        private bool _requireLos = true;
        private TargetSelector _selector = TargetSelector.HighestThreat;
        private Positioning _positioning = Positioning.Melee(2f);
        private float _leash = 25f;
        private MobAction? _action;

        internal MobBrainBuilder(string mobType) => _mobType = mobType ?? throw new ArgumentNullException(nameof(mobType));

        /// <summary>Sets the aggro/sense radius and whether line of sight is required to sense a player.</summary>
        public MobBrainBuilder Perceive(float aggroRadius, bool requireLos = true)
        {
            _aggroRadius = aggroRadius; _requireLos = requireLos; return this;
        }

        /// <summary>Chooses how the brain picks its target.</summary>
        public MobBrainBuilder SelectTarget(TargetSelector selector) { _selector = selector; return this; }

        /// <summary>Chooses the positioning mode and leash radius (distance from spawn past which the mob resets).</summary>
        public MobBrainBuilder Position(Positioning positioning, float leash)
        {
            _positioning = positioning ?? throw new ArgumentNullException(nameof(positioning));
            _leash = leash; return this;
        }

        /// <summary>Sets the action performed once in position.</summary>
        public MobBrainBuilder Act(MobAction action) { _action = action; return this; }

        /// <summary>Builds the configured brain. Implicitly convertible so it can be passed straight to <c>Register</c>.</summary>
        public IMobBrain Build()
            => new ComposedBrain(_mobType, _aggroRadius, _requireLos, _selector, _positioning, _leash, _action);

        /// <summary>Implicitly builds the brain, so a builder can be handed directly to <c>MobServer.Register</c>.</summary>
        public static implicit operator ComposedBrain(MobBrainBuilder b) => (ComposedBrain)b.Build();
    }

    /// <summary>
    /// The brain produced by <see cref="MobBrain.Compose"/>: a small state machine over the fixed
    /// perceive→select→position→act pipeline. Its perception parameters (aggro radius, LOS requirement) are read by
    /// the framework so the shared <see cref="MobSenses"/> is built to match.
    /// </summary>
    public sealed class ComposedBrain : MobBrain
    {
        private readonly string _mobType;
        private readonly TargetSelector _selector;
        private readonly Positioning _positioning;
        private readonly float _leash;
        private readonly MobAction? _action;

        /// <summary>The aggro/sense radius this brain wants (read by the framework when building senses).</summary>
        public float AggroRadius { get; }

        /// <summary>Whether this brain requires line of sight to sense players (read by the framework).</summary>
        public bool RequireLos { get; }

        /// <summary>The leash radius from spawn past which the brain drops its target and resets.</summary>
        public float LeashRadius => _leash;

        internal ComposedBrain(string mobType, float aggroRadius, bool requireLos, TargetSelector selector,
            Positioning positioning, float leash, MobAction? action)
        {
            _mobType = mobType;
            AggroRadius = aggroRadius;
            RequireLos = requireLos;
            _selector = selector;
            _positioning = positioning;
            _leash = leash;
            _action = action;
        }

        /// <inheritdoc/>
        public override string MobType => _mobType;

        /// <inheritdoc/>
        public override async Task ThinkAsync(MobContext ctx, MobSenses senses)
        {
            var mob = ctx.Mob;

            // Leash: if we've strayed too far from spawn, drop the fight and head home.
            if (!senses.InLeashRange)
            {
                if (mob.Target != null) { ctx.SetTarget(null); }
                ctx.MoveTo(mob.SpawnPoint);
                return;
            }

            // Select a target if we don't have a valid one.
            var target = senses.Target;
            if (target == null)
            {
                target = MobTargeting.Pick(senses, _selector);
                if (target != null) ctx.SetTarget(target.PlayerKey);
                else return;   // nothing to do
            }

            var dist = target.Distance;
            ctx.Face(target.Position);

            if (_positioning.IsKite)
            {
                // Keep the target inside [MinRange, Range].
                if (dist < _positioning.MinRange)
                {
                    // Too close: back away from the target.
                    var away = (mob.Position - target.Position);
                    ctx.MoveTo(mob.Position + (away.LengthSquared > 1e-6f ? away.Normalized : new Vec3(1, 0, 0)) * _positioning.MinRange);
                }
                else if (dist > _positioning.Range)
                {
                    ctx.MoveTo(target.Position);   // close in
                }
                else if (_action != null && target.HasLineOfSight)
                {
                    await ctx.UseAbilityAsync(_action.AbilityId, target.PlayerKey).ConfigureAwait(false);
                }
            }
            else
            {
                // Melee/approach: close to Range, then attack.
                if (dist > _positioning.Range)
                {
                    ctx.MoveTo(target.Position);
                }
                else if (_action != null)
                {
                    await ctx.UseAbilityAsync(_action.AbilityId, target.PlayerKey).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc/>
        public override Task OnDamagedAsync(MobContext ctx, DamageEvent dmg)
        {
            // Threat is added by the framework before this fires; latch onto the attacker if idle.
            if (ctx.Mob.Target == null && !string.IsNullOrEmpty(dmg.Source))
                ctx.SetTarget(dmg.Source);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task OnTargetLostAsync(MobContext ctx)
        {
            ctx.SetTarget(null);
            return Task.CompletedTask;
        }
    }

    /// <summary>Shared target-picking logic used by the composed brain and the archetype brains.</summary>
    internal static class MobTargeting
    {
        /// <summary>Picks a target from the sensed players per the selector (highest threat, then nearest as a tie-break/fallback).</summary>
        public static PerceivedPlayer? Pick(MobSenses senses, TargetSelector selector)
        {
            PerceivedPlayer? best = null;
            if (selector == TargetSelector.Nearest)
            {
                var bestDist = float.PositiveInfinity;
                foreach (var p in senses.Nearby)
                    if (p.Distance < bestDist) { bestDist = p.Distance; best = p; }
                return best;
            }

            // HighestThreat: pick the max threat; if nobody has threat yet, fall back to nearest.
            var bestThreat = float.NegativeInfinity;
            var anyThreat = false;
            foreach (var p in senses.Nearby)
            {
                if (p.Threat > bestThreat) { bestThreat = p.Threat; best = p; }
                if (p.Threat > 0) anyThreat = true;
            }
            if (!anyThreat) return Pick(senses, TargetSelector.Nearest);
            return best;
        }
    }
}
