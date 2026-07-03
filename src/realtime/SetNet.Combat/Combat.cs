using System;
using SetNet.Stats;

namespace SetNet.Combat
{
    /// <summary>
    /// The stat keys combat reads from a <see cref="StatSet"/>. Defaults match common RPG names, but you can point
    /// them at whatever your <see cref="StatSchema"/> actually uses — combat never hard-codes a stat vocabulary.
    /// </summary>
    public sealed class CombatStatKeys
    {
        /// <summary>Attacker's raw offensive stat. Default "attack_power".</summary>
        public string AttackPower { get; set; } = "attack_power";
        /// <summary>Defender's mitigation stat. Default "defense".</summary>
        public string Defense { get; set; } = "defense";
        /// <summary>Attacker's crit chance (0..1). Default "crit_chance".</summary>
        public string CritChance { get; set; } = "crit_chance";
        /// <summary>Attacker's extra crit damage fraction (0.5 = +50%). Default "crit_mult".</summary>
        public string CritMultiplier { get; set; } = "crit_mult";
    }

    /// <summary>Describes one attack independent of who throws it: the coefficient on attack power, a flat bonus, its type, and whether it can crit.</summary>
    public sealed class AttackSpec
    {
        /// <summary>Multiplier applied to the attacker's attack power (an ability's scaling). Default 1.</summary>
        public double Coefficient { get; set; } = 1.0;
        /// <summary>A flat amount added before mitigation.</summary>
        public double FlatBonus { get; set; } = 0.0;
        /// <summary>The damage type (your own string: "physical", "fire"…). Passed through to the result.</summary>
        public string DamageType { get; set; } = "physical";
        /// <summary>Whether this attack may crit. Default true.</summary>
        public bool CanCrit { get; set; } = true;

        /// <summary>Creates an attack spec.</summary>
        public AttackSpec() { }
        /// <summary>Creates an attack spec with a coefficient + type.</summary>
        public AttackSpec(double coefficient, string damageType = "physical", double flatBonus = 0)
        { Coefficient = coefficient; DamageType = damageType; FlatBonus = flatBonus; }
    }

    /// <summary>The outcome of resolving an attack: how much damage, its type, whether it crit, and how much armor absorbed.</summary>
    public readonly struct DamageResult
    {
        /// <summary>Final damage to apply (≥ 0).</summary>
        public double Amount { get; }
        /// <summary>The damage type carried from the <see cref="AttackSpec"/>.</summary>
        public string DamageType { get; }
        /// <summary>True if the hit was a critical.</summary>
        public bool IsCrit { get; }
        /// <summary>How much raw damage the defender's mitigation absorbed.</summary>
        public double Mitigated { get; }

        /// <summary>Creates a result.</summary>
        public DamageResult(double amount, string damageType, bool isCrit, double mitigated)
        { Amount = amount; DamageType = damageType; IsCrit = isCrit; Mitigated = mitigated; }
    }

    /// <summary>Everything a formula needs to resolve one attack.</summary>
    public sealed class CombatContext
    {
        /// <summary>The attacker's stats (may be null for environmental/fixed damage).</summary>
        public StatSet? Attacker { get; }
        /// <summary>The defender's stats (may be null for unmitigated damage).</summary>
        public StatSet? Defender { get; }
        /// <summary>The attack being thrown.</summary>
        public AttackSpec Spec { get; }
        /// <summary>Which stat keys to read.</summary>
        public CombatStatKeys Keys { get; }
        /// <summary>RNG for crit rolls (seedable for deterministic tests).</summary>
        public Random Rng { get; }

        /// <summary>Creates a context.</summary>
        public CombatContext(StatSet? attacker, StatSet? defender, AttackSpec spec, CombatStatKeys keys, Random rng)
        { Attacker = attacker; Defender = defender; Spec = spec; Keys = keys; Rng = rng; }
    }

    /// <summary>Pluggable damage math. Swap it to change your game's combat feel without touching the rest.</summary>
    public interface ICombatFormula
    {
        /// <summary>Resolves an attack into a <see cref="DamageResult"/>.</summary>
        DamageResult Resolve(CombatContext ctx);
    }

    /// <summary>
    /// A sensible default formula: raw = attackPower·coefficient + flatBonus, an optional crit multiplies it, then the
    /// defender's armor mitigates via <c>raw · (armorConstant / (armorConstant + defense))</c>, clamped to at least
    /// <see cref="MinDamage"/>. Tune <see cref="ArmorConstant"/> / <see cref="MinDamage"/> or replace the whole formula.
    /// </summary>
    public sealed class StandardCombatFormula : ICombatFormula
    {
        /// <summary>Armor softening constant — higher means defense mitigates less per point. Default 100.</summary>
        public double ArmorConstant { get; set; } = 100.0;
        /// <summary>The minimum damage any landed hit deals. Default 1.</summary>
        public double MinDamage { get; set; } = 1.0;

        /// <inheritdoc/>
        public DamageResult Resolve(CombatContext ctx)
        {
            var k = ctx.Keys;
            var atk = ctx.Attacker?.Get(k.AttackPower) ?? 0;
            var raw = atk * ctx.Spec.Coefficient + ctx.Spec.FlatBonus;

            var isCrit = false;
            if (ctx.Spec.CanCrit && ctx.Attacker != null)
            {
                var chance = ctx.Attacker.Get(k.CritChance);
                if (chance > 0 && ctx.Rng.NextDouble() < chance)
                {
                    isCrit = true;
                    raw *= 1 + ctx.Attacker.Get(k.CritMultiplier);
                }
            }

            var defense = ctx.Defender?.Get(k.Defense) ?? 0;
            var mitigatedFraction = defense > 0 ? defense / (ArmorConstant + defense) : 0;
            var mitigated = raw * mitigatedFraction;
            var final = raw - mitigated;
            if (final < MinDamage) final = MinDamage;

            return new DamageResult(final, ctx.Spec.DamageType, isCrit, mitigated);
        }
    }

    /// <summary>A simple current/max health pool that <see cref="CombatResolver"/> applies damage to.</summary>
    public sealed class Health
    {
        /// <summary>Current health.</summary>
        public double Current { get; private set; }
        /// <summary>Maximum health.</summary>
        public double Max { get; private set; }
        /// <summary>True while <see cref="Current"/> &gt; 0.</summary>
        public bool IsAlive => Current > 0;
        /// <summary>Current as a fraction of max (0..1).</summary>
        public double Fraction => Max > 0 ? Current / Max : 0;

        /// <summary>Raised when health changes (current, max).</summary>
        public event Action<double, double>? Changed;
        /// <summary>Raised once when the pool drops to zero.</summary>
        public event Action? Died;

        /// <summary>Creates a full pool.</summary>
        public Health(double max) { Max = max; Current = max; }

        /// <summary>Applies damage; returns true if this hit killed the entity.</summary>
        public bool Apply(double amount)
        {
            if (amount <= 0 || !IsAlive) return false;
            Current -= amount;
            if (Current < 0) Current = 0;
            Changed?.Invoke(Current, Max);
            if (Current == 0) { Died?.Invoke(); return true; }
            return false;
        }

        /// <summary>Heals up to max (no effect once dead).</summary>
        public void Heal(double amount)
        {
            if (amount <= 0 || !IsAlive) return;
            Current += amount;
            if (Current > Max) Current = Max;
            Changed?.Invoke(Current, Max);
        }

        /// <summary>Sets a new maximum, clamping current into range.</summary>
        public void SetMax(double max) { Max = max < 0 ? 0 : max; if (Current > Max) Current = Max; Changed?.Invoke(Current, Max); }
        /// <summary>Refills to full.</summary>
        public void Revive() { Current = Max; Changed?.Invoke(Current, Max); }
    }

    /// <summary>
    /// The entry point for combat: resolve an attack between two <see cref="StatSet"/>s and (optionally) apply it to a
    /// <see cref="Health"/> pool. Reuse one resolver across your whole server; it is stateless except the RNG.
    /// </summary>
    public sealed class CombatResolver
    {
        private readonly ICombatFormula _formula;
        private readonly CombatStatKeys _keys;
        private readonly Random _rng;

        /// <summary>Creates a resolver with an optional custom formula, stat keys and RNG (seed for deterministic tests).</summary>
        public CombatResolver(ICombatFormula? formula = null, CombatStatKeys? keys = null, Random? rng = null)
        {
            _formula = formula ?? new StandardCombatFormula();
            _keys = keys ?? new CombatStatKeys();
            _rng = rng ?? new Random();
        }

        /// <summary>Resolves an attack into a damage result (does not apply it).</summary>
        public DamageResult Resolve(StatSet? attacker, StatSet? defender, AttackSpec spec)
            => _formula.Resolve(new CombatContext(attacker, defender, spec, _keys, _rng));

        /// <summary>Resolves an attack and applies it to <paramref name="targetHealth"/>; the result's death flag is out via <paramref name="died"/>.</summary>
        public DamageResult ResolveAndApply(StatSet? attacker, StatSet? defender, AttackSpec spec, Health targetHealth, out bool died)
        {
            var result = Resolve(attacker, defender, spec);
            died = targetHealth.Apply(result.Amount);
            return result;
        }
    }
}
