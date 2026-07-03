<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Abilities

**Server-authoritative abilities and skills for [SetNet](https://www.nuget.org/packages/SetNet).**

Define abilities with a cooldown, resource cost, range and target kind, plus composable effects — `DamageEffect` (through [`SetNet.Combat`](https://www.nuget.org/packages/SetNet.Combat)), `HealEffect`, and `BuffEffect` (a timed [`SetNet.Stats`](https://www.nuget.org/packages/SetNet.Stats) modifier). Clients request an ability; the server validates cooldown/cost/target/range and applies the effects. Entity stats, health, positions and resources all come through **seams**, so mobs and players run through the exact same system. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Abilities
```

## Usage

Call `AbilitiesRuntime.Enable()` once at startup on both ends.

**Server** — wire the seams to your entity state, then `Define` abilities:

```csharp
AbilitiesRuntime.Enable();
var abilities = server.UseAbilities(new AbilityOptions
{
    StatsOf    = key => world.StatsOf(key),      // string key -> StatSet?
    HealthOf   = key => world.HealthOf(key),     // string key -> Health?
    PositionOf = key => world.PositionOf(key),   // for range checks (null disables them)
    ResourceOf = (key, id) => world.ResourceOf(key, id),   // mana/energy pools
});

abilities.Define(new AbilityDefinition
{
    Id = "fireball", CooldownMs = 3000, Range = 30,
    ResourceId = "mana", ResourceCost = 20,
    TargetKind = TargetKind.Target,
    Effects = { new DamageEffect(coefficient: 1.5, damageType: "fire") },
});

// server/mob logic can fire it directly through the same entry point:
AbilityOutcome outcome = await abilities.TryUseAsync("mob:dragon#7", "fireball", AbilityTarget.Of(playerKey));
```

**Client** — request a use; the server is the authority:

```csharp
AbilitiesRuntime.Enable();
var abilities = client.UseAbilities();
var outcome = await abilities.UseAsync("fireball", targetKey: enemyId);
if (!outcome.Ok) Toast(outcome.Message);   // "on cooldown", "out of range", "not enough mana", …
else StartCooldownUi(outcome.CooldownMs);
```

## Notes

- **`TryUseAsync` is the one gate.** Both client requests and server/mob logic go through it: it validates cooldown → target → range → resource cost, starts the cooldown *before* applying (so a throwing effect can't be spammed), then runs each effect. A bad effect is caught so it can't abort the rest.
- **Composable effects.** Drop `DamageEffect`, `HealEffect`, `BuffEffect` into `AbilityDefinition.Effects`, or implement `IAbilityEffect` for your own. `BuffEffect` applies re-tagged stat modifiers and auto-removes them after its duration.
- **Seams, not subclasses.** `AbilityOptions` resolves stats/health/position/resource from entity keys, so the same abilities work for players and mobs. Set `PlayerKey` to map a peer to its stable key (default = connection id — override to the authenticated account id, matching your other hubs).
- **Targeting.** `TargetKind` (`None`/`Self`/`Target`/`Point`) plus `AbilityTarget.Of(key)` / `At(point)`. Range is enforced only when `PositionOf` is set.
- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Abilities` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. The control protocol is hand-framed `byte[]`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
