<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Locomotion

**One unified server-side tick that moves everything — players, mobs, NPCs, projectiles — and replicates nothing.**

Create a `Mover` and it's *automatically* part of the tick; the system advances its position along a pathfound route
N times per second. It sends **nothing** over the network — you read positions and replicate them your own way. When a
mover gets a new destination it fires `Started`, so you can send just the **destination point** to clients (L2-style;
the client re-paths locally — see [SetNet.Locomotion.Unity](https://github.com/Povstalez/SetNet)).

## Server

```csharp
IGeoData geo = GeoDataFile.LoadFromFile("world.geo");
var loco = server.UseLocomotion(geo, new LocomotionOptions { Hz = 10 });   // one unified system

// send just the destination to nearby clients when something starts moving (client re-paths from the point):
loco.Started += m => SendMoveTo(m.Owner, m.Destination!.Value);

// …or send the route the mover is actually walking, so clients don't re-run the same search. Worth it in a crowd:
// every client otherwise pays one pathfinding run per moving entity, and the orders arrive together.
loco.Started += m => SendRoute(m.Owner, m.Waypoints);          // m.WaypointIndex = how much is already behind

// anything that moves creates a Mover — that's the whole subscription:
var mover = loco.CreateMover(spawnPos, speed: stats.Get("move_speed"), owner: character);

// on a client move command / mob AI decision:
mover.GoTo(destination);      // paths there, starts following (false if unreachable)

// the system advances mover.Position every tick — read it for range/AoI or to write into your replicated state:
mover.DestinationReached += m => { /* arrived */ };
mover.Dispose();              // auto-unsubscribe when the entity leaves
```

## What it is (and isn't)

- **It simulates, it doesn't replicate.** No packets, no StateSync writes — just positions advancing. You decide how the position reaches clients (a NetworkVariable field, an L2 "move to point" event on `Started`, whatever).
- **Automatic subscription.** `CreateMover` registers it; `Dispose()` removes it. No manual `Register`.
- **Unified.** Players, NPCs, projectiles — anything holding a `Mover` shares the one tick. (Mobs has its own tick; point its movement here too if you want a single system.)
- **One reused pathfinder** (`Pathfinding.For(geo)`, pooled) for every mover.

## API

`server.UseLocomotion(geo, options?)` (or `new LocomotionSystem(geo, options?)`) → `LocomotionSystem`:

- `CreateMover(start, speed, owner?)` → `Mover` (auto-subscribed), `Movers`, `Count`.
- `Update(dtMs)` when `UseInternalTimer = false` (drive it from your own loop).
- `Started` event — a mover got a new destination (send the point to clients here).

`Mover`: `Position`, `Speed`, `Owner`, `Destination`, `IsMoving`, `GoTo(point)`, `Stop()`, `Warp(point)`, `DestinationReached`, `Dispose()`.

## Notes

- **Server-authoritative** — `GoTo` paths on the server; an unreachable point does nothing.
- **L2-style pairing:** replicate only the destination on `Started`; the client runs its own `FindPath` and walks the model with the Unity `NavAgent`. Between destinations the server sends nothing.
- Uses `SetNet.GeoData.Vec3`.

## License

MIT
