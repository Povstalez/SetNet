# SetNet.StateSync.SpatialGrid

**Spatial-grid interest management for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

An `IInterestManager` that buckets entities into a uniform 3D grid, so each observer tests only the entities in nearby cells — **O(neighbours)** per observer instead of O(N). Use it for large worlds with many entities (a faster drop-in for the built-in `DistanceInterest`).

```csharp
var options = new StateSyncOptions
{
    Interest = new SpatialGridInterest(
        entityPosition:   e    => e.GetVec3(0),
        observerPosition: peer => FocusOf(peer),
        radius:   60f,
        cellSize: 60f)
};
server.UseStateSync(options);
```

The grid is rebuilt once per tick and reused across observers. `alwaysSeeOwnedEntities` keeps an observer's own entities visible outside the radius.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
