<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet GeoData Baker for Unity

**A Unity editor tool that bakes a portable [SetNet.GeoData](https://www.nuget.org/packages/SetNet.GeoData) file (`.geo`) from your scene — one menu, one button.**

Point it at a scene, pick a mode, click Bake. It writes an engine-independent binary blob that a **headless SetNet server** loads at boot via `GeoDataFile.LoadFromFile("world.geo")` to answer walkability, line-of-sight, and pathable queries — with **no runtime engine dependency**. The baked file is just geometry; nothing from Unity ships with your server.

> This is a **Unity (UPM) source package** — a small **Editor** assembly (the bakers + window) plus a tiny **runtime** assembly (the debug visualizer). Not a NuGet package and not built by `dotnet`. The bakers only *write* the GeoData format, so they need **no** reference to the `SetNet.GeoData` assembly; the server side is where those live.

## Four bake modes

| Mode | Source | Manual work | Output |
|---|---|---|---|
| **From NavMesh** | `NavMesh.CalculateTriangulation()` — the scene's already-baked NavMesh | just have a NavMesh baked | a **nav-mesh** `.geo` (triangles) |
| **From Colliders** | a downward `Physics.Raycast` per grid cell | cell size + walkable/obstacle `LayerMask`s + bounds | a **grid** `.geo` (walkable/blocked cells + heights) |
| **Layered (Colliders)** | a downward `Physics.RaycastAll` per cell — keeps **every** surface | same as Colliders + layer tolerances | a **multi-storey** `.geo` (stacked height layers → floors/bridges, no nav-mesh) |
| **Sectors** | tiles the world and bakes each tile (grid or layered) | + a sector size | **many** `.geo` files + a **`.geomap`** manifest for one seamless `SectoredGeoData` |

- **From NavMesh** is zero-config beyond baking a NavMesh (Window → AI → Navigation, or a `NavMeshSurface`). Precise for irregular 3D worlds.
- **From Colliders** casts a ray straight down over each cell: a **walkable-layer** hit → a walkable cell at the hit height; an **obstacle-layer** hit (or a surface steeper than *Max Slope*) → a blocked cell; nothing → empty. Single-surface.
- **Layered (Colliders)** is the answer to **multi-storey worlds (этажність) on a grid**: it fires a *stack* of downward rays per cell and keeps **every** walkable surface as a separate height layer (de-duped within *Layer Merge Epsilon*), so buildings, bridges and overpasses bake **without a nav-mesh**. Loads as a `LayeredGridGeoData`. Set *Max Step* to your stair height and *Layer Match Tolerance* to how far above a floor a query still counts as "on" it.
- **Sectors** is the automated path for **large, zone-divided worlds**: give it a *Sector Size* and it tiles the world, bakes each tile (as grid or layered), and writes a `.geomap` manifest. The server rebuilds one seamless world from the manifest.

World bounds can **auto-fit** to the scene's renderers (one click) or be entered manually — the automation goal is "pick a mode, press Bake".

## Debug visualizer — *see* what got baked

After any bake the window offers **Add Debug Visualizer to Scene**: it drops a `GeoDataGizmo` component that reads the baked file (or manifest) and draws it in the Scene view — **green** walkable cells at their real height, **red** walls/blocked cells, each **stacked layer** of a multi-storey grid, **cyan** nav-mesh triangles, and a **yellow** outline per sector for a manifest. Point it at any `.geo`/`.geomap` yourself (path or a `.bytes` `TextAsset`), toggle what to draw, and hit **Reload GeoData** after re-baking. It's how you confirm the geodata matches the level before shipping it to the server.

## Install

1. Add this package to your Unity project via UPM (Package Manager → **Add package from git URL…**), pointing at the `src/engine/SetNet.GeoData.Unity` folder of the SetNet repo, or copy the folder under your project's `Packages/` (or `Assets/`).
2. That's it — the tool is editor-only and self-contained. The `SetNet` / `SetNet.GeoData` assemblies are **not** needed in the Unity project; they live on your server.

## Use

1. Open **SetNet → Bake GeoData** from the menu bar.
2. Pick **Mode**: *NavMesh*, *Colliders*, *Layered (Colliders)*, or *Sectors*.
3. Set the **Output Path** (e.g. `Assets/world.geo`; for *Sectors* the file name becomes the manifest/sector base name).
4. Set the mode's options (cell size, layer masks, slope/step; layered adds tolerances; sectors add a sector size), pick *Auto Bounds* or enter bounds, then click **Bake**. A cancelable progress bar runs.
5. The window shows a summary, then offers **Add Debug Visualizer to Scene** to inspect the result.

Then on the server — a single file, or a whole sectored world:

```csharp
using SetNet.GeoData;

// one baked file (grid / layered / nav-mesh):
IGeoData geo = GeoDataFile.LoadFromFile("world.geo");

// OR a sectored world from the manifest — one seamless IGeoData over every sector:
IGeoData world = GeoDataManifest.Load("world.geomap");

bool stand   = world.IsWalkable(pos);
bool canSee  = world.LineOfSight(mob, player);
Vec3 snapped = world.SampleNearestWalkable(offMesh);
var path     = SetNet.PathFinding.Pathfinding.For(world).FindPath(a, b);   // routes across sectors
```

## Coordinates

Unity is **Y-up, left-handed**; SetNet's `Vec3` is also **Y-up** with X/Y/Z laid out identically, so the baker writes positions **component-wise as-is** — no axis flip or handedness swap. Distances and heights carry over 1:1.

## File format

The tool emits the exact binary that `SetNet.GeoData.GeoDataFile` reads:

- **Header:** `S N G D` (4 ASCII bytes), version `1`, kind (`1` = grid, `2` = nav-mesh, `3` = layered grid).
- **Grid:** origin (3 floats), cell size (float), width (int32), depth (int32), max step (float), cell count (int32), then per cell **row-major (z outer, x inner)** a flags byte (bit 0 walkable, bit 1 blocked) + a height float.
- **Layered grid:** origin, cell size, width, depth, max step, layer-match-tolerance, then per cell **row-major** a wall bool + a layer count (int32) + that many `height (float), walkable (byte)` layers.
- **Nav-mesh:** vertex count (int32) + that many `X,Y,Z` float triples, index count (int32) + that many int32 triangle indices.
- **Manifest (`.geomap`):** `S N G M` (4 ASCII bytes), version `1`, entry count (int32), then per sector a length-prefixed UTF-8 id + relative `.geo` path + min `X,Y,Z` + max `X,Y,Z` floats. Load with `GeoDataManifest.Load(path)`.

All values are little-endian (`System.IO.BinaryWriter`), matching the server's `BinaryReader`.

## License

MIT
