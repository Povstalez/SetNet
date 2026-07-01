<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.SpatialGrid

**Grid-accelerated interest management for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

[`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync) ships a built-in `DistanceInterest` that scopes replication to an area of interest around each observer — but it tests **every entity against every observer**, which is O(N) per observer and O(N·observers) per tick. Fine for a few dozen entities; it falls over in a large world with thousands of entities and many players.

`SpatialGridInterest` is a **drop-in replacement** that buckets entities into a uniform 3D grid once per tick and only tests the entities in the cells near each observer — **O(neighbours) per observer** instead of O(N). The grid is built once (cached by the entity-list instance the server passes to every observer that tick) and reused across all observers, so the per-tick cost stays flat as the world grows.

Same contract as any `IInterestManager`: entities entering an observer's set are spawned on that client, entities leaving are despawned.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.StateSync.SpatialGrid
```

## Usage

Assign a `SpatialGridInterest` to `StateSyncOptions.Interest` when you enable replication on the server. You supply how to read an entity's position and an observer's focus position — the core doesn't know which field is "position".

```csharp
using SetNet.StateSync;
using SetNet.StateSync.SpatialGrid;

// e.g. field 0 of the Player archetype is a Vector3 position.
// Track each peer's current focus (camera/character position) however your game does.
var focusByPeer = new Dictionary<Guid, Vec3>();

var world = server.UseStateSync(new StateSyncOptions
{
    TickRate = 30,
    Interest = new SpatialGridInterest(
        entityPosition:   e    => e.GetVec3(0),                          // NetworkEntity → Vec3
        observerPosition: peer => focusByPeer[peer.CurrentPeerInfo.Id],  // BasePeer → Vec3
        radius:   50f,   // world units an observer can see
        cellSize: 0,     // 0 → uses radius; a good default is ~the radius
        alwaysSeeOwnedEntities: true)  // owner always sees its own entities, even far away
});
```

That's the only change — everything else (spawning, mutating fields, snapshots, the client) is unchanged from `SetNet.StateSync`. As the server ticks, each observer receives spawns/despawns as entities move in and out of its grid neighbourhood.

## Options

| Constructor parameter | Type | Default | Meaning |
|---|---|---|---|
| `entityPosition` | `Func<NetworkEntity, Vec3>` | — | Reads an entity's world position |
| `observerPosition` | `Func<BasePeer, Vec3>` | — | Reads an observer's focus/camera position |
| `radius` | `float` | — | Visibility radius in world units |
| `cellSize` | `float` | `0` | Grid cell edge; `≤ 0` uses `radius`. Tune to ~the radius |
| `alwaysSeeOwnedEntities` | `bool` | `true` | Observer always sees entities it owns, even outside the radius |

## Notes

- **Position selectors are required** because the core replication layer doesn't know which field holds a position — you tell it. Read the same field you declared as the position in your `ReplicaSchema` (e.g. `e.GetVec3(0)`).
- **`cellSize` tuning:** cells much smaller than the radius mean scanning many cells per query; cells much larger than the radius mean each cell holds far-away entities you then reject by distance. Roughly `cellSize ≈ radius` is a solid starting point.
- **Grid reuse:** the grid is rebuilt only when the server hands the manager a new entity-list instance (once per tick), so querying many observers in the same tick shares one build. It is thread-safe (guarded internally).
- **Drop-in:** the constructor signature mirrors `DistanceInterest` (plus `cellSize`), so switching a large world from `DistanceInterest` to `SpatialGridInterest` is a one-line change with identical visibility semantics.
- Cell coordinates are packed into a `long` key (21 bits per axis, biased), which comfortably covers typical world extents.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
