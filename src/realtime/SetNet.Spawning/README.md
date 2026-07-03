# SetNet.Spawning

**Zone-based mob spawning for [SetNet](https://www.nuget.org/packages/SetNet), on top of [SetNet.Mobs](https://www.nuget.org/packages/SetNet.Mobs).**

Define spawn zones as box or circle areas, say which mob types live in each zone, how many of each to keep alive, and how
long after a death one respawns. The server keeps every zone populated — counting living mobs, scheduling a respawn for
any that died, and spawning replacements when their delay elapses — snapping spawn points to walkable ground via
[SetNet.GeoData](https://www.nuget.org/packages/SetNet.GeoData) when you supply it. Headless-drivable or self-timed.

```csharp
var mobs = server.UseMobs(mobOptions);       // your SetNet.Mobs hub, brains already registered

var spawning = server.UseSpawning(mobs, new SpawnOptions
    {
        GeoData          = world,            // optional: snap spawn points to walkable ground
        UseInternalTimer = true,             // default — or false to drive Update(dt) yourself
        Seed             = 1234,             // optional deterministic RNG
    })
    .AddZone(SpawnZone.Circle("forest", center, radius: 40f)
        .Add("goblin", count: 8,  respawnMs: 30000)
        .Add("wolf",   count: 3,  respawnMs: 45000))
    .AddZone(SpawnZone.Box("cave", min, max)
        .Add("bat",    count: 12, respawnMs: 15000, health: 20));

// If UseInternalTimer = false, tick it from your own loop:
spawning.Update(dtMs);
```

## Concepts

- **Zones** — `SpawnZone.Circle(id, center, radius)` / `SpawnZone.Box(id, min, max)`; the id is also set as each spawned
  mob's `Zone`. Custom shapes: subclass `SpawnArea` and pass it to `new SpawnZone(id, area)`.
- **Populations** — `zone.Add(mobType, count, respawnMs, health?, faction?)` declares one mob type to keep alive. The
  `mobType` matches a brain's `MobType` you registered on the `MobServer`. A zone can hold several populations.
- **Filling & respawning** — each tick the server tops zones up to `count`, and when a mob dies it schedules a respawn
  `respawnMs` later. It spawns mobs with their own respawn disabled (`RespawnMs = 0`) so **this** server owns the timing.
- **Driving it** — the internal `Timer` runs at `TickIntervalMs` (default 1000 ms) by default; set
  `UseInternalTimer = false` and call `SpawningServer.Update(dtMs)` from your game loop instead. `SpawningServer` is
  `IDisposable` (stops the timer).

## Notes

- **Server-side, no wire protocol of its own.** Spawns flow through `MobServer.Spawn`, so clients see them via the usual
  `SetNet.Mobs` events / replication. `SpawningRuntime.Enable()` exists for symmetry and is a no-op.
- Depends on `SetNet` + `SetNet.Mobs` + `SetNet.GeoData`. `GeoData` is optional — without it, spawn points are used as-is;
  with it, each point is `SampleNearestWalkable`-snapped to the ground.

## License

MIT
