using System;
using System.Collections.Generic;

namespace SetNet.Stats
{
    /// <summary>How a <see cref="StatModifier"/> combines with the running value.</summary>
    public enum ModifierOp
    {
        /// <summary>A flat amount added to the base (all flats sum together first).</summary>
        Flat = 0,
        /// <summary>An additive percentage — all of these sum, then multiply once (e.g. +10% and +20% → ×1.30).</summary>
        PercentAdd = 1,
        /// <summary>A multiplicative percentage — each applies in turn (e.g. +10% then +20% → ×1.10×1.20).</summary>
        PercentMult = 2,
    }

    /// <summary>One stat in a schema: its key, default base value, and clamp range.</summary>
    public sealed class StatDefinition
    {
        /// <summary>The stat key (your own string, e.g. "attack_power", "move_speed").</summary>
        public string Id { get; }
        /// <summary>The default base value before any modifiers.</summary>
        public double Base { get; }
        /// <summary>The lowest a computed value may be (default -∞).</summary>
        public double Min { get; }
        /// <summary>The highest a computed value may be (default +∞).</summary>
        public double Max { get; }

        /// <summary>Defines a stat.</summary>
        public StatDefinition(string id, double baseValue = 0, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Base = baseValue; Min = min; Max = max;
        }
    }

    /// <summary>
    /// A named, reusable set of <see cref="StatDefinition"/>s — your game's stat vocabulary. Share one schema across
    /// every player and every mob type; each entity gets its own <see cref="StatSet"/> instance over it. Fully custom:
    /// you decide which stats exist and their ranges.
    /// </summary>
    public sealed class StatSchema
    {
        private readonly Dictionary<string, StatDefinition> _defs;

        private StatSchema(Dictionary<string, StatDefinition> defs) => _defs = defs;

        /// <summary>Starts a fluent schema builder.</summary>
        public static Builder Create() => new Builder();

        /// <summary>The definition for a stat, or null if the schema doesn't declare it.</summary>
        public StatDefinition? Get(string statId) => statId != null && _defs.TryGetValue(statId, out var d) ? d : null;

        /// <summary>All defined stat keys.</summary>
        public IEnumerable<string> StatIds => _defs.Keys;

        /// <summary>Creates a fresh <see cref="StatSet"/> over this schema.</summary>
        public StatSet NewSet() => new StatSet(this);

        /// <summary>Fluent builder for a <see cref="StatSchema"/>.</summary>
        public sealed class Builder
        {
            private readonly Dictionary<string, StatDefinition> _defs = new Dictionary<string, StatDefinition>();

            /// <summary>Declares a stat with a base value and optional clamp range.</summary>
            public Builder Define(string id, double baseValue = 0, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
            {
                _defs[id] = new StatDefinition(id, baseValue, min, max);
                return this;
            }

            /// <summary>Builds the immutable schema.</summary>
            public StatSchema Build() => new StatSchema(new Dictionary<string, StatDefinition>(_defs));
        }
    }

    /// <summary>
    /// A single modifier on a stat: an operation, a value, and an optional <see cref="Source"/> tag so a group of
    /// modifiers (e.g. everything a piece of equipment or a buff grants) can be removed together.
    /// </summary>
    public sealed class StatModifier
    {
        /// <summary>The stat this modifier affects.</summary>
        public string StatId { get; }
        /// <summary>How it combines.</summary>
        public ModifierOp Op { get; }
        /// <summary>The amount (a flat number, or a fraction for percent ops — 0.1 = +10%).</summary>
        public double Value { get; }
        /// <summary>Optional grouping tag (equip slot id, buff id…) for <see cref="StatSet.RemoveBySource"/>.</summary>
        public object? Source { get; }

        /// <summary>Creates a modifier.</summary>
        public StatModifier(string statId, ModifierOp op, double value, object? source = null)
        {
            StatId = statId ?? throw new ArgumentNullException(nameof(statId));
            Op = op; Value = value; Source = source;
        }

        /// <summary>Shorthand for a flat modifier.</summary>
        public static StatModifier Flat(string statId, double value, object? source = null) => new StatModifier(statId, ModifierOp.Flat, value, source);
        /// <summary>Shorthand for an additive-percent modifier (0.1 = +10%).</summary>
        public static StatModifier PercentAdd(string statId, double value, object? source = null) => new StatModifier(statId, ModifierOp.PercentAdd, value, source);
        /// <summary>Shorthand for a multiplicative-percent modifier (0.1 = ×1.1).</summary>
        public static StatModifier PercentMult(string statId, double value, object? source = null) => new StatModifier(statId, ModifierOp.PercentMult, value, source);
    }

    /// <summary>
    /// One entity's live statistics over a <see cref="StatSchema"/>. A stat's value is computed as
    /// <c>clamp( (base + Σflat) · (1 + ΣpercentAdd) · Π(1 + percentMult) )</c>. Add/remove modifiers (from equipment,
    /// buffs, level-ups…) and read the resolved value; results are cached until modifiers change.
    /// </summary>
    public sealed class StatSet
    {
        private readonly StatSchema _schema;
        private readonly List<StatModifier> _mods = new List<StatModifier>();
        private readonly Dictionary<string, double> _baseOverride = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _cache = new Dictionary<string, double>();

        /// <summary>Raised when a value may have changed. The argument is the affected stat id, or null for a bulk change.</summary>
        public event Action<string?>? Changed;

        /// <summary>Creates a set over a schema.</summary>
        public StatSet(StatSchema schema) => _schema = schema ?? throw new ArgumentNullException(nameof(schema));

        /// <summary>The schema this set is built over.</summary>
        public StatSchema Schema => _schema;

        /// <summary>The active modifiers (read-only).</summary>
        public IReadOnlyList<StatModifier> Modifiers => _mods;

        /// <summary>Overrides the base value of a stat (before modifiers), e.g. from a level-up.</summary>
        public void SetBase(string statId, double value)
        {
            _baseOverride[statId] = value;
            Invalidate(statId);
        }

        /// <summary>Clears a base override, reverting to the schema's default base.</summary>
        public void ResetBase(string statId)
        {
            if (_baseOverride.Remove(statId)) Invalidate(statId);
        }

        /// <summary>Adds a modifier and invalidates its stat's cached value.</summary>
        public void AddModifier(StatModifier modifier)
        {
            if (modifier == null) throw new ArgumentNullException(nameof(modifier));
            _mods.Add(modifier);
            Invalidate(modifier.StatId);
        }

        /// <summary>Adds several modifiers at once.</summary>
        public void AddModifiers(IEnumerable<StatModifier> modifiers)
        {
            if (modifiers == null) return;
            foreach (var m in modifiers) _mods.Add(m);
            _cache.Clear();
            Changed?.Invoke(null);
        }

        /// <summary>Removes a specific modifier instance.</summary>
        public bool RemoveModifier(StatModifier modifier)
        {
            if (_mods.Remove(modifier)) { Invalidate(modifier.StatId); return true; }
            return false;
        }

        /// <summary>Removes every modifier tagged with <paramref name="source"/> (equal by <see cref="object.Equals(object)"/>).</summary>
        public int RemoveBySource(object source)
        {
            var removed = _mods.RemoveAll(m => Equals(m.Source, source));
            if (removed > 0) { _cache.Clear(); Changed?.Invoke(null); }
            return removed;
        }

        /// <summary>Removes all modifiers (base overrides are kept).</summary>
        public void ClearModifiers()
        {
            if (_mods.Count == 0) return;
            _mods.Clear();
            _cache.Clear();
            Changed?.Invoke(null);
        }

        /// <summary>The resolved value of a stat (0 for an unknown stat with no modifiers).</summary>
        public double Get(string statId)
        {
            if (statId == null) return 0;
            if (_cache.TryGetValue(statId, out var cached)) return cached;
            var value = Compute(statId);
            _cache[statId] = value;
            return value;
        }

        /// <summary>The resolved value rounded to the nearest whole number (for integer stats like damage).</summary>
        public long GetInt(string statId) => (long)Math.Round(Get(statId));

        private double Compute(string statId)
        {
            var def = _schema.Get(statId);
            var baseValue = _baseOverride.TryGetValue(statId, out var ov) ? ov : (def?.Base ?? 0);

            double flat = 0, percentAdd = 0;
            var value = baseValue;
            // Two passes so percent-mult applies after flats + additive-percents regardless of insertion order.
            foreach (var m in _mods)
            {
                if (m.StatId != statId) continue;
                if (m.Op == ModifierOp.Flat) flat += m.Value;
                else if (m.Op == ModifierOp.PercentAdd) percentAdd += m.Value;
            }
            value = (baseValue + flat) * (1 + percentAdd);
            foreach (var m in _mods)
                if (m.StatId == statId && m.Op == ModifierOp.PercentMult)
                    value *= (1 + m.Value);

            if (def != null)
            {
                if (value < def.Min) value = def.Min;
                if (value > def.Max) value = def.Max;
            }
            return value;
        }

        private void Invalidate(string statId)
        {
            _cache.Remove(statId);
            Changed?.Invoke(statId);
        }
    }
}
