# SetNet.PathFinding

**Pathfinding + path-following for [SetNet](https://www.nuget.org/packages/SetNet), over [SetNet.GeoData](https://www.nuget.org/packages/SetNet.GeoData).**

Find a route, then walk an entity along it — server-side and engine-agnostic. One interface, `IPathfinder`, with the
right algorithm picked from the geometry kind:

- **grid** → A* (8-connected, octile, no corner-cutting) + straight-line smoothing
- **multi-storey grid** → A* over per-cell height *layers* (climbs stairs / crosses bridges between floors) + smoothing
- **nav-mesh** → A* over triangles + portal path + straight-line smoothing

```csharp
IGeoData geo = GeoDataFile.LoadFromFile("world.geo");
IPathfinder finder = Pathfinding.For(geo);            // grid or nav-mesh — automatic

Path path = finder.FindPath(mobPos, playerPos);
if (!path.IsEmpty)
{
    var follower = new PathFollower(path);
    // each server tick:
    mobPos = follower.Step(mobPos, speed * dtSeconds);   // moves toward the next waypoint
    if (follower.Arrived) { /* reached the goal */ }
}
```

## Built for scale (path everyone, every tick)

In an MMO every character and mob may re-path several times a second, so the hot path is **allocation-free after
warm-up**. A pathfinder instance is meant to be **built once and reused** (that's exactly how `SetNet.Mobs` holds it):

- Per-query working memory (`g`/`came`/closed sets, the open heap) is **pooled** and reused across calls — nothing is
  allocated per `FindPath` once the pool is warm.
- Visited nodes are **generation-stamped** instead of cleared, so a query costs *O(nodes actually expanded)*, not
  *O(map size)* — a short path on a huge map stays cheap.
- The pool is thread-safe, so one pathfinder can serve many agents from parallel AI ticks.
- `MaxExpansions` (on `GridPathfinder` / `LayeredGridPathfinder`) caps the worst case: an unreachable goal returns
  `Path.Empty` instead of scanning the whole reachable area.

```csharp
var finder = Pathfinding.For(geo);        // build ONCE, keep it
// ...thousands of times, from any thread:
var path = finder.FindPath(a, b);         // no per-call array allocation
```

The `World` example (`dotnet run --project examples/World -- bench`) reports throughput on a reused instance.

## Notes

- **Server-side, no wire protocol.** Movement is server-authoritative; clients see replicated positions.
- `PathFollower` is what **[SetNet.Mobs](https://www.nuget.org/packages/SetNet.Mobs)** uses to turn a `MoveTo` intent into authoritative motion — but Mobs treats the pathfinder as an optional seam (straight-line fallback when no `IGeoData` is provided).
- Nav-mesh routing uses a portal-midpoint path with line-of-walk smoothing (a robust stand-in for a full funnel).

## License

MIT
