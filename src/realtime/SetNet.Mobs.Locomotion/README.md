<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Mobs.Locomotion

**Make [SetNet.Mobs](https://www.nuget.org/packages/SetNet.Mobs) move through the unified [SetNet.Locomotion](https://www.nuget.org/packages/SetNet.Locomotion) tick.**

By default a mob steps its own position with its built-in path-follower. This opt-in bridge hands that stepping to a
shared `LocomotionSystem` instead — so **players and mobs move in the same tick**, and both fire the system's `Started`
hook, letting you replicate them the same way (send just the destination, L2-style). The mob AI, perception, threat,
combat and replication are all unchanged; only *where the position advances* moves out.

## Use

```csharp
var loco = server.UseLocomotion(geo);                 // the one unified movement system

var mobs = server.UseMobs(new MobOptions
{
    GeoData = geo,
    // …perception seams…
    Mover = loco.AsMobMover(),                        // ← mobs now advance through Locomotion
});

// players create Movers on the same system, so one hook replicates everything:
loco.Started += m => SendMoveTo(m.Owner, m.Destination!.Value);   // m.Owner is the MobInstance for a mob
```

Leave `MobOptions.Mover` unset to keep the built-in mob movement — nothing else changes either way.

## How it works

`AsMobMover()` wraps the `LocomotionSystem` as an `IMobMover` (the movement seam in SetNet.Mobs). Per mob it:

- **spawns** a `Mover` at the mob's position with `MobOptions.MoveSpeed`, owned by the `MobInstance`;
- on a new brain move-goal, calls `mover.GoTo(goal)` (which fires `Started`);
- reads `mover.Position` back into `mob.Position` each AI tick;
- disposes the `Mover` when the mob dies / despawns.

## Notes

- **Two ticks, one purpose.** Mobs still runs its AI tick (decisions); Locomotion runs the movement tick (stepping). The mob's position lags the mover by at most one tick — fine, and exactly how L2 separates AI from movement.
- **Unified replication.** Because a mob is now a `Mover` with the mob as `Owner`, `LocomotionSystem.Started` gives you one place to send "move to point" for players and mobs alike.
- Uses `SetNet.GeoData.Vec3`.

## License

MIT
