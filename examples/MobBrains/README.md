# MobBrains example

Three mobs, three AI styles, **one** tick loop, **one** movement system, **one** service locator:

| Mob | AI | Behaviour |
|---|---|---|
| `shadow` | **SetNet.BehaviorTree** — `Selector( Sequence(dist>7 → chase), hold )` | trails the player at a ~7 standoff |
| `lunger` | **SetNet.StateMachine** — `advance ⇄ hold` (hysteresis) | lunges in to ~4, holds, lunges again as you pull away |
| `follower` | plain | walks right onto the player |

What it demonstrates:

- **Everything runs through `SetNet.Ticks`.** `new TickScheduler().MakeCurrent()` is set up *before* the systems, so `LocomotionSystem` (channel `locomotion` @15 Hz) and `MobServer` (channel `mobs` @10 Hz) **auto-subscribe** — no `Update()` calls, no manual registration. Each mob's behaviour tree / state machine ticks inside its `ThinkAsync`, which itself runs on the `mobs` channel.
- **Both mobs walk through `SetNet.Locomotion`.** `MobOptions.Mover = loco.AsMobMover()` routes mob movement through the *same* `LocomotionSystem` as the player (via the `SetNet.Mobs.Locomotion` bridge).
- **A moving player**, itself just a `Mover` on that system, walking a loop.
- **`SetNet.Services` locator.** Every brain reads the player's position with `Service.Get<PlayerRef>()` — no brain stores a reference; the player is registered once in the hub.

## Run

```bash
dotnet run --project examples/MobBrains
```

Sample output (distances to the player):

```
t= 4.0s  player=( 34, 44)   shadow d= 5.5   lunger d= 3.6 [advance]   follower d= 1.1
t= 4.5s  player=( 36, 44)   shadow d= 4.4   lunger d= 2.4 [hold]      follower d= 0.8
t= 6.5s  player=( 44, 44)   shadow d= 5.3   lunger d= 9.7 [advance]   follower d= 0.9
```

`follower` stays ~0–1 away, `shadow` holds a ~4–7 standoff, `lunger` visibly toggles `hold`/`advance`.

## Notes

- Mobs move faster than the player and start next to it, so they stay inside Mobs' **keep-awake radius** — `SetNet.Mobs` sleeps unobserved mobs, which is what the `PlayerPosition` seam feeds. (Custom brains use a 12-unit default gate.)
- No networking: one process so you can watch the AI. In a real game the mob positions replicate your usual way and the client re-paths from the destination point (L2-style); here we just print positions.
- The player position is provided **twice on purpose**: to `MobOptions.PlayerPosition` (so Mobs' sleep gate/perception knows a player is near) and to the `ServiceHub` (so the *brains* read the exact target). A real server does the same — the engine's perception seam and your gameplay code are different consumers.
