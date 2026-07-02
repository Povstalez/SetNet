# SetNet.GeoData

**Server-side world geometry for [SetNet](https://www.nuget.org/packages/SetNet) — walkability, line-of-sight, pathable queries.**

The server's knowledge of the scene: *where can you stand*, *what blocks sight or movement*, *where is the ground*.
One interface, `IGeoData`, backed by three implementations:

- **`GridGeoData`** — a 2.5D nav-grid (walkable/blocked cells + ground height). Cheap, and easy to bake automatically from colliders. Single-surface (one height per cell).
- **`LayeredGridGeoData`** — a **multi-storey** nav-grid (the classic Lineage-2-style "geodata"): a cell can hold **several stacked walkable layers**, so you get floors, bridges and overpasses **without a nav-mesh**. Every query is height-aware. This is the grid answer to *етажність*.
- **`NavMeshGeoData`** — a nav-mesh (triangles + adjacency). Precise for irregular 3D worlds; exposes portals so `SetNet.PathFinding` can funnel a smooth path. Also Y-aware for multi-storey.

```csharp
IGeoData geo = GeoDataFile.LoadFromFile("world.geo");   // baked once (see SetNet.GeoData.Unity)

bool stand   = geo.IsWalkable(pos);
bool canSee  = geo.LineOfSight(mob, player);       // "can A see B"
bool canWalk = geo.CanWalkStraight(from, to);      // "can I walk straight there"
Vec3 snapped = geo.SampleNearestWalkable(offMesh);
RaycastHit h = geo.Raycast(origin, dir, 50f);
```

Build a grid by hand (or from a baker):

```csharp
var geo = new GridGeoDataBuilder(origin: Vec3.Zero, cellSize: 1f, width: 64, depth: 64)
    .Fill((x, z) => (walkable: !IsWall(x, z), blocked: IsWall(x, z), height: 0f))
    .Build();
GeoDataFile.SaveToFile(geo, "world.geo");
```

Or a nav-mesh from triangles (e.g. an exported engine NavMesh):

```csharp
var geo = NavMeshGeoData.FromTriangles(vertices, triangleIndices);
```

## Height, stairs & multi-storey (етажність)

- **Height differences & slopes** — every representation carries real heights. `SampleHeight` returns the ground Y; `MaxStep` bounds how big a height change between adjacent cells still counts as walkable.
- **Stairs / steps** — a staircase is just a run of surfaces each within `MaxStep` of the next (grid/layered grid), or connected triangles (nav-mesh). `CanWalkStraight` enforces the step limit so an agent won't "walk straight" up a cliff.
- **Multi-storey / overhangs on a GRID** (a bridge over a road, a building's floors stacked at the same XZ) — use **`LayeredGridGeoData`**. Each cell holds one or more height *layers*; every query carries the agent's Y and resolves to the layer nearest that height, so an agent on the ground floor never snaps onto the deck above it. This is how L2-style servers do floors **without a nav-mesh**:

  ```csharp
  var geo = new LayeredGridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 64, depth: 64)
      .SetMaxStep(1.1f)                 // one stair step
      .AddLayer(cx, cz, height: 0f)     // ground floor at this cell
      .AddLayer(cx, cz, height: 4f)     // upper storey stacked at the SAME cell
      // .SetWall(cx, cz)               // a full-height wall (blocks sight + movement at every height)
      .Build();

  geo.SampleHeight(new Vec3(x, 0.1f, z));   // -> 0   (nearest layer to y=0.1 = ground)
  geo.SampleHeight(new Vec3(x, 3.8f, z));   // -> 4   (nearest layer to y=3.8 = upper storey)
  Pathfinding.For(geo).FindPath(groundPos, upperPos);   // climbs the stairs between floors
  ```

  Movement/height queries are fully layer-accurate. **Line-of-sight** is occluded by wall cells and by any floor whose height lies strictly between the two endpoints and that the ray passes through (so you can't see a target on the storey above through the floor) — for arbitrary opaque ceilings/soffits use a nav-mesh.
- **Multi-storey on a NAV-MESH** — `NavMeshGeoData` is Y-aware too: `TriangleAt`/`SampleHeight`/`IsWalkable` resolve to the floor nearest the query's Y, and `CanWalkStraight` (bounded by `WalkYTolerance`) won't jump between storeys through the air. Bake it from an engine NavMesh (which already models floors) via `SetNet.GeoData.Unity`.
- **Plain `GridGeoData` is single-surface** (one height per cell) — great for terrain with slopes/steps; for overlapping floors reach for `LayeredGridGeoData` or the nav-mesh.

See the runnable **`World`** example (`dotnet run --project examples/World -- floors`) for a two-storey grid + cross-floor pathfinding.

## Sectored worlds (zones / sectors)

A big world is usually split into **sectors** (zones), each baked separately. `SectoredGeoData` stitches them into one
seamless `IGeoData`, dispatching each query to the sector that owns the point — walkability, height, sight and
can-walk-straight all work **across sector borders**:

```csharp
var world = new SectoredGeoDataBuilder()
    .Add("x0_z0", GeoDataFile.LoadFromFile("world_x0_z0.geo"))
    .Add("x1_z0", GeoDataFile.LoadFromFile("world_x1_z0.geo"))
    .Build();

// …or, from a baked manifest (the Unity sector baker writes one) — loads every sector in one call:
IGeoData world = GeoDataManifest.Load("world.geomap");

world.IsWalkable(p);                                   // routed to the owning sector
SetNet.PathFinding.Pathfinding.For(world).FindPath(a, b);   // paths across sectors (delegates within, stitches at borders)
```

Sectors can be a mix of grid / layered / nav-mesh, and can even **stack in Y** (a dungeon under a field). Pathfinding
within a sector is exact (its native pathfinder, built once and reused); cross-sector routing walks the sector graph
and stitches per-sector paths through the shared borders. Bake sectors + the manifest automatically with the Unity tool.

## Notes

- **Server-side library, no wire protocol** — the world is server-authoritative; clients render replicated state, they don't query GeoData over the network.
- **Engine-agnostic** — depends only on `SetNet`, ships its own `Vec3`; convert at the edges. The [Unity tool](https://www.nuget.org/packages/SetNet.GeoData.Unity) bakes a `GeoDataFile` from a scene's NavMesh or colliders.
- Foundation for **[SetNet.PathFinding](https://www.nuget.org/packages/SetNet.PathFinding)** and **[SetNet.Mobs](https://www.nuget.org/packages/SetNet.Mobs)**.

## License

MIT
