<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Stats

**Custom character statistics for [SetNet](https://www.nuget.org/packages/SetNet).**

Define your own stat vocabulary — attack power, defense, move/attack speed, whatever your game needs — as a reusable `StatSchema`, then give any entity (players **or** mobs) a `StatSet` over it. Each stat's value is computed from a base plus **flat**, **additive-percent** and **multiplicative-percent** modifiers, clamped to the schema's range and cached until something changes. Modifiers carry an optional source tag, so everything a buff or a piece of gear grants can be pulled off together. This is the foundation [`SetNet.Combat`](https://www.nuget.org/packages/SetNet.Combat), [`SetNet.Abilities`](https://www.nuget.org/packages/SetNet.Abilities) and [`SetNet.Equipment`](https://www.nuget.org/packages/SetNet.Equipment) build on. Server-side, no wire protocol.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Stats
```

## Usage

Declare the schema once, then spin up a set per entity:

```csharp
// your game's stat vocabulary (share one schema across every player and mob type)
var schema = StatSchema.Create()
    .Define("attack_power", baseValue: 10)
    .Define("defense",      baseValue: 0)
    .Define("crit_chance",  baseValue: 0, min: 0, max: 1)
    .Build();

var stats = schema.NewSet();

// gear and buffs add modifiers, tagged with a source so they come off together:
stats.AddModifier(StatModifier.Flat("attack_power", 5, source: "sword"));
stats.AddModifier(StatModifier.PercentAdd("attack_power", 0.20, source: "rage")); // +20%

double atk = stats.Get("attack_power");  // (10 + 5) * 1.20 = 18
long   dmg = stats.GetInt("attack_power"); // rounded to a whole number

stats.RemoveBySource("rage");  // strip the buff; cached values recompute lazily
```

## Notes

- **Fully custom.** SetNet.Stats ships no stat names or ranges — you declare them. Every downstream module (Combat/Abilities/Equipment) reads stats by the keys *you* chose.
- **Modifier math.** A value resolves as `clamp( (base + Σflat) · (1 + ΣpercentAdd) · Π(1 + percentMult) )`, so ordering is deterministic regardless of insertion order. Use `StatModifier.Flat` / `PercentAdd` / `PercentMult` (0.1 = +10%).
- **Remove by source.** Tag a group of modifiers with any object (a buff id, an equip slot) and drop them all at once with `RemoveBySource(source)` — how equipment and timed buffs clean up after themselves.
- **Base overrides.** `SetBase(statId, value)` overrides a stat's base (e.g. from a level-up) before modifiers apply; `ResetBase` reverts to the schema default.
- **Cached + observable.** Resolved values are cached until a relevant modifier changes; subscribe to `StatSet.Changed` (the affected stat id, or `null` for a bulk change) to react.
- **Server-side only.** No runtime bootstrap, no wire types — it's a plain library. Replication of the *results* is your game's job (e.g. via [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync)).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
