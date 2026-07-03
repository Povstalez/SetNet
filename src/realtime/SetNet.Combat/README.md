<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Combat

**Server-authoritative combat resolution for [SetNet](https://www.nuget.org/packages/SetNet).**

Turn an attacker's and a defender's [`StatSet`](https://www.nuget.org/packages/SetNet.Stats)s into a `DamageResult` — attack-power scaling, armor mitigation, crit rolls — through a pluggable `ICombatFormula`, then apply it to a `Health` pool. Which stats mean "attack power" / "defense" / "crit" is fully configurable via `CombatStatKeys`, so combat never hard-codes a stat vocabulary and fits whatever schema you defined. Server-side, no wire protocol.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Combat
```

## Usage

Build one resolver and reuse it across the whole server — it's stateless except its RNG:

```csharp
var combat = new CombatResolver();   // StandardCombatFormula + default CombatStatKeys
var attackerHp = new Health(100);
var targetHp   = new Health(100);

// a basic swing: 1.0× the attacker's attack power, physical, can crit
var spec = new AttackSpec(coefficient: 1.0, damageType: "physical");

DamageResult hit = combat.ResolveAndApply(attackerStats, targetStats, spec, targetHp, out bool died);
Console.WriteLine($"{hit.Amount} dmg{(hit.IsCrit ? " (crit!)" : "")}, {hit.Mitigated} absorbed");
if (died) OnKill();

targetHp.Changed += (cur, max) => Redraw(cur / max);
```

## Notes

- **Configurable stat keys.** `CombatStatKeys` maps combat's inputs (`AttackPower`, `Defense`, `CritChance`, `CritMultiplier`) onto your own [`SetNet.Stats`](https://www.nuget.org/packages/SetNet.Stats) keys — defaults are common RPG names, override them to match your schema.
- **Pluggable math.** `StandardCombatFormula` computes `raw = attackPower·coefficient + flatBonus`, rolls an optional crit, then mitigates with `raw · (armorConstant / (armorConstant + defense))`, clamped to `MinDamage`. Tune `ArmorConstant`/`MinDamage`, or implement `ICombatFormula` to change the feel entirely.
- **`AttackSpec` describes the attack, not the attacker.** A coefficient on attack power, a flat bonus, a damage type and whether it can crit — reused by abilities to scale spells off the caster's stats.
- **`Health` pool.** Current/max with `Apply` (returns true on the killing blow), `Heal`, `SetMax`, `Revive`, plus `Changed`/`Died` events. `Resolve` computes without applying; `ResolveAndApply` does both and reports death.
- **Deterministic tests.** Seed the resolver's `Random` for reproducible crit rolls.
- **Server-side only.** No runtime bootstrap, no wire types — a plain library. Building blocks for [`SetNet.Abilities`](https://www.nuget.org/packages/SetNet.Abilities) and [`SetNet.Mobs`](https://www.nuget.org/packages/SetNet.Mobs).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
