<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StatusEffects

**Server-authoritative buffs and debuffs for [SetNet](https://www.nuget.org/packages/SetNet).**

Apply timed, stacking status effects (poison, haste, shield…) to any **target key** — a player or an entity id. The server owns the effects, a timer expires them, and every change is pushed to the affected player and to anyone **watching** that target (so players fighting a boss see its debuffs tick down). Stacking and refresh behaviour is configurable per effect type. This layer tracks the effects; interpreting a magnitude (armor bonus, damage-over-time) is your game logic. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StatusEffects
```

## Usage

Call `StatusEffectRuntime.Enable()` once at startup on both ends.

```csharp
// server
StatusEffectRuntime.Enable();
var effects = server.UseStatusEffects();
effects.Define(new StatusEffectDefinition("poison", maxStacks: 5, defaultDurationMs: 8000, stacking: StackPolicy.Stack, isDebuff: true));
effects.Define(new StatusEffectDefinition("haste",  defaultDurationMs: 5000, stacking: StackPolicy.Refresh));

// game logic (an ability hits a player or a mob entity id):
await effects.ApplyAsync(targetKey: victimPlayerKey, "poison", stacks: 2, magnitude: 12, source: casterKey);
await effects.ApplyAsync(targetKey: "mob:dragon#7", "haste", durationMs: 3000);
await effects.RemoveAsync(victimPlayerKey, "poison");   // a cleanse

// client
StatusEffectRuntime.Enable();
var effects = client.UseStatusEffects();
effects.Changed += (target, list) => DrawEffectBar(target, list);

await effects.WatchAsync(myPlayerKey);       // my own buffs/debuffs
await effects.WatchAsync("mob:dragon#7");    // the boss I'm fighting
```

## API

**Server:** `server.UseStatusEffects(StatusEffectOptions?)` → `StatusEffectServer` (`Dispose()` stops the timer)

| Member | Purpose |
|---|---|
| `Define(StatusEffectDefinition)` | register stack/refresh/duration rules for an effect type |
| `ApplyAsync(targetKey, effectId, durationMs = 0, stacks = 1, magnitude = 0, source = "")` | apply/refresh an effect |
| `RemoveAsync(targetKey, effectId)` / `ClearAsync(targetKey)` | cleanse |
| `GetAsync(targetKey)` | current effects |

**Client:** `client.UseStatusEffects()` → `StatusEffectClient` — `GetAsync(targetKey)`, `WatchAsync(targetKey)`, `UnwatchAsync(targetKey)`, `event Changed`.

**Definition:** `EffectId`, `MaxStacks`, `DefaultDurationMs` (0 = permanent), `Stacking` (`Refresh`/`Stack`/`Ignore`), `IsDebuff`.

`StatusEffectRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65448 / 65449 / 65450.** Don't reuse them.
- **Target keys are arbitrary.** A player key (the affected player gets pushes automatically) or an entity id like `"mob:dragon#7"` (no peer — read via `GetAsync`, or let nearby players `WatchAsync` it). By default the player key is the connection id; override `StatusEffectOptions.TargetKey`.
- **Stacking policies.** `Refresh` resets duration and keeps the higher stack count; `Stack` adds stacks up to `MaxStacks` and refreshes; `Ignore` drops a re-application while one is active. Unregistered effects default to single-stack refresh.
- **Effects are just data here.** The `Magnitude` and stack count are yours to interpret in combat/stat code. Pair with [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync) if you also replicate entity transforms, and drive interest (who watches a mob) from your spatial logic.
- **Not persisted.** Effects live in memory and expire on the timer; that's the usual lifetime for combat buffs. Re-apply on respawn/zone-in as your design requires.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
