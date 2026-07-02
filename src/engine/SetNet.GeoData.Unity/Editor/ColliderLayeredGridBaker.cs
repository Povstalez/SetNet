using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Bakes a <b>multi-storey</b> (Lineage-2-style) layered grid by sweeping <i>every</i> walkable surface under each
    /// cell — not just the top one. Per cell it fires a stack of downward rays (via <see cref="Physics.RaycastAll"/>),
    /// keeps each walkable-layer hit whose slope is gentle enough as a separate height layer, and marks the cell a
    /// full-height wall when the only thing under it is an obstacle. The result loads on a server as a
    /// <c>LayeredGridGeoData</c>, so floors, bridges and overpasses work without a nav-mesh.
    /// </summary>
    public static class ColliderLayeredGridBaker
    {
        /// <summary>Inputs for a layered collider bake.</summary>
        public struct Settings
        {
            /// <summary>Cell edge length in world units.</summary>
            public float CellSize;
            /// <summary>Layers whose colliders count as walkable ground (each hit surface becomes a layer).</summary>
            public LayerMask WalkableMask;
            /// <summary>Layers whose colliders are full-height walls.</summary>
            public LayerMask ObstacleMask;
            /// <summary>World-space area to sample on the XZ plane (Y bounds the ray sweep).</summary>
            public Bounds WorldBounds;
            /// <summary>Largest surface slope (degrees from up) still walkable; steeper surfaces are skipped.</summary>
            public float MaxSlopeDegrees;
            /// <summary>Largest climbable step between adjacent cell layers (stored as MaxStep).</summary>
            public float MaxStep;
            /// <summary>How close (world Y) a query must be to a layer to count as standing on it (stored as LayerMatchTolerance).</summary>
            public float LayerMatchTolerance;
            /// <summary>Two surfaces closer than this in Y are merged into one layer (de-dupes coincident colliders).</summary>
            public float LayerMergeEpsilon;
        }

        /// <summary>Summary of a layered bake.</summary>
        public readonly struct Result
        {
            /// <summary>Cells along X.</summary>
            public readonly int Width;
            /// <summary>Cells along Z.</summary>
            public readonly int Depth;
            /// <summary>Total walkable layers across all cells.</summary>
            public readonly int LayerCount;
            /// <summary>Cells flagged as full-height walls.</summary>
            public readonly int WallCells;

            /// <summary>Creates a result.</summary>
            public Result(int width, int depth, int layerCount, int wallCells)
            {
                Width = width; Depth = depth; LayerCount = layerCount; WallCells = wallCells;
            }

            /// <summary>Total cell count (width*depth).</summary>
            public int TotalCells => Width * Depth;
        }

        /// <summary>
        /// Sweeps the grid and writes a layered-grid GeoData file to <paramref name="outputPath"/>.
        /// <paramref name="onProgress"/> (0..1) is called per row (return <c>false</c> to cancel).
        /// </summary>
        public static bool Bake(
            string outputPath,
            Settings settings,
            Func<float, bool> onProgress,
            out Result result,
            out string error)
        {
            result = default;
            error = null;

            if (settings.CellSize <= 0f) { error = "Cell size must be greater than zero."; return false; }
            var size = settings.WorldBounds.size;
            if (size.x <= 0f || size.z <= 0f)
            {
                error = "World bounds are empty on the XZ plane. Set explicit bounds or ensure the scene has renderers to auto-fit.";
                return false;
            }

            var width = Mathf.Max(1, Mathf.CeilToInt(size.x / settings.CellSize));
            var depth = Mathf.Max(1, Mathf.CeilToInt(size.z / settings.CellSize));
            var count = width * depth;
            var origin = new Vector3(settings.WorldBounds.min.x, settings.WorldBounds.min.y, settings.WorldBounds.min.z);

            var rayStartY = settings.WorldBounds.max.y + 1f;
            var rayLength = size.y + 2f;
            var cosMaxSlope = Mathf.Cos(Mathf.Deg2Rad * Mathf.Clamp(settings.MaxSlopeDegrees, 0f, 90f));
            var mergeEps = Mathf.Max(0.001f, settings.LayerMergeEpsilon);

            // CSR accumulation: gather per-cell layers, then flatten.
            var cellLayers = new List<float>[count];   // walkable surface heights per cell (sorted later)
            var walls = new bool[count];
            var totalLayers = 0;
            var wallCells = 0;

            var walkMask = settings.WalkableMask.value;
            var obstacleMask = settings.ObstacleMask.value;
            var combined = walkMask | obstacleMask;

            for (var cz = 0; cz < depth; cz++)
            {
                for (var cx = 0; cx < width; cx++)
                {
                    var idx = cz * width + cx;   // row-major: cz outer, cx inner.
                    var cxCenter = origin.x + (cx + 0.5f) * settings.CellSize;
                    var czCenter = origin.z + (cz + 0.5f) * settings.CellSize;
                    var rayStart = new Vector3(cxCenter, rayStartY, czCenter);

                    var hits = Physics.RaycastAll(rayStart, Vector3.down, rayLength, combined, QueryTriggerInteraction.Ignore);
                    if (hits.Length == 0) continue;   // empty cell (void) — no floor, no wall.
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));   // top-down

                    List<float> layers = null;
                    var sawObstacle = false;
                    foreach (var hit in hits)
                    {
                        var bit = 1 << hit.collider.gameObject.layer;
                        if ((obstacleMask & bit) != 0) { sawObstacle = true; continue; }   // obstacles don't make layers
                        if ((walkMask & bit) == 0) continue;
                        if (Vector3.Dot(hit.normal, Vector3.up) < cosMaxSlope) continue;    // too steep to stand on

                        var y = hit.point.y;
                        layers ??= new List<float>(2);
                        // Merge with an existing near-coincident surface.
                        var merged = false;
                        for (var k = 0; k < layers.Count; k++)
                            if (Mathf.Abs(layers[k] - y) <= mergeEps) { merged = true; break; }
                        if (!merged) layers.Add(y);
                    }

                    if (layers != null && layers.Count > 0)
                    {
                        cellLayers[idx] = layers;
                        totalLayers += layers.Count;
                    }
                    else if (sawObstacle)
                    {
                        walls[idx] = true;   // only an obstacle here → full-height wall.
                        wallCells++;
                    }
                }

                if (onProgress != null && !onProgress((cz + 1) / (float)depth))
                {
                    error = "Bake cancelled.";
                    return false;
                }
            }

            // Flatten to CSR arrays (layers sorted ascending per cell, matching LayeredGridGeoData).
            var cellStart = new int[count + 1];
            var layerHeights = new float[totalLayers];
            var layerWalkable = new byte[totalLayers];
            var k2 = 0;
            for (var i = 0; i < count; i++)
            {
                cellStart[i] = k2;
                var list = cellLayers[i];
                if (list != null)
                {
                    list.Sort();
                    foreach (var y in list) { layerHeights[k2] = y; layerWalkable[k2] = 1; k2++; }
                }
            }
            cellStart[count] = k2;

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(outputPath))
                GeoDataFileWriter.WriteLayeredGrid(fs, origin, settings.CellSize, width, depth,
                    settings.MaxStep, settings.LayerMatchTolerance, cellStart, layerHeights, layerWalkable, walls);

            result = new Result(width, depth, totalLayers, wallCells);
            return true;
        }
    }
}
