# SetNet.Projectiles

Server-authoritative **travelling projectiles** (arrows, rockets, grenades) for SetNet — with the **same pluggable hit test** as [`SetNet.Hitscan`](https://www.nuget.org/packages/SetNet.Hitscan).

Each tick a projectile advances and **sweeps the segment** from its old to its new position through your `IHitDetector`, so it can't tunnel through thin targets. It replicates nothing — you read positions / handle `Hit` and send your own way.

```csharp
using SetNet.Projectiles;
using SetNet.Hitscan;

// same IHitDetector you use for hitscan (yours, or the shipped detectors)
var world = new ProjectileSystem(detector, new ProjectileOptions
{
    Gravity = new Vec3(0, -9.81f, 0),   // omit for straight-line shots
});

world.Hit     += (p, hit) => Damage(hit.TargetId!, ((Weapon)p.Tag!).Damage);
world.Expired += p => { /* fizzle out */ };

world.Spawn(new ProjectileSpawn
{
    Origin = muzzle, Direction = aimDir, Speed = 40f,
    OwnerId = "player", MaxDistance = 100, LifetimeMs = 5000, Tag = weapon,
});
```

- **Bring your own hit detection** via `IHitDetector` (see `SetNet.Hitscan`) — targets, world geometry, or your own physics.
- **Sweep, don't teleport** — the per-step raycast catches fast projectiles vs thin targets.
- **Gravity / ballistics** — `ProjectileOptions.Gravity` curves the path; velocity updates each step.
- **Lifetime & range** — `LifetimeMs` / `MaxDistance` auto-expire.
- **Auto-ticks** — implements `SetNet.Ticks.ITickable`: set `new TickScheduler().MakeCurrent()` and it drives itself (channel `projectiles` @ `Hz`); or `UseInternalTimer`, or call `Update(dtMs)` from your loop.

`Projectile`: `Position`/`Velocity`/`OwnerId`/`Traveled`/`Alive`/`Tag`. Events: `Spawned`, `Hit(Projectile, HitResult)`, `Expired(Projectile)`.

Depends on `SetNet.Hitscan` + `SetNet.GeoData` + `SetNet.Ticks`. No wire. **License:** MIT.
