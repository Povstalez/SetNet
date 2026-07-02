using System;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Bakes a 2.5D grid GeoData file by raycasting straight down over each cell of a world-bounds rectangle.
    /// Per cell: a walkable-layer hit → walkable (at the hit height); an obstacle-layer hit or a too-steep surface
    /// normal → blocked; nothing → empty (neither flag).
    /// </summary>
    public static class ColliderGridBaker
    {
        /// <summary>Inputs for a collider grid bake.</summary>
        public struct Settings
        {
            /// <summary>Cell edge length in world units.</summary>
            public float CellSize;
            /// <summary>Layers whose colliders count as walkable ground.</summary>
            public LayerMask WalkableMask;
            /// <summary>Layers whose colliders block a cell.</summary>
            public LayerMask ObstacleMask;
            /// <summary>World-space area to sample, on the XZ plane (Y is used for the ray start/length).</summary>
            public Bounds WorldBounds;
            /// <summary>Largest surface slope (degrees from up) still considered walkable; steeper ground is blocked.</summary>
            public float MaxSlopeDegrees;
            /// <summary>Largest ground-height step an agent may traverse between adjacent cells (stored as MaxStep).</summary>
            public float MaxStep;
        }

        /// <summary>Summary of a collider grid bake.</summary>
        public readonly struct Result
        {
            /// <summary>Cells along X.</summary>
            public readonly int Width;
            /// <summary>Cells along Z.</summary>
            public readonly int Depth;
            /// <summary>Cells flagged walkable.</summary>
            public readonly int WalkableCells;
            /// <summary>Cells flagged blocked.</summary>
            public readonly int BlockedCells;

            /// <summary>Creates a result.</summary>
            public Result(int width, int depth, int walkable, int blocked)
            {
                Width = width;
                Depth = depth;
                WalkableCells = walkable;
                BlockedCells = blocked;
            }

            /// <summary>Total cell count (width*depth).</summary>
            public int TotalCells => Width * Depth;
        }

        /// <summary>
        /// Raycasts the grid and writes it to <paramref name="outputPath"/> as a grid GeoData file.
        /// <paramref name="onProgress"/> (0..1) is called per row so callers can drive a progress bar and cancel
        /// (return <c>false</c> to abort). Returns <c>false</c> with <paramref name="error"/> set on invalid input
        /// or cancellation.
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

            if (settings.CellSize <= 0f)
            {
                error = "Cell size must be greater than zero.";
                return false;
            }

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

            // Cast from just above the bounds top, downward through the whole vertical extent (+ margin).
            var rayStartY = settings.WorldBounds.max.y + 1f;
            var rayLength = size.y + 2f;

            var flags = new byte[count];
            var heights = new float[count];
            var walkableCount = 0;
            var blockedCount = 0;

            var cosMaxSlope = Mathf.Cos(Mathf.Deg2Rad * Mathf.Clamp(settings.MaxSlopeDegrees, 0f, 90f));

            for (var cz = 0; cz < depth; cz++)
            {
                for (var cx = 0; cx < width; cx++)
                {
                    var idx = cz * width + cx; // row-major: cz outer, cx inner — matches the reader.

                    var cellCenterX = origin.x + (cx + 0.5f) * settings.CellSize;
                    var cellCenterZ = origin.z + (cz + 0.5f) * settings.CellSize;
                    var rayStart = new Vector3(cellCenterX, rayStartY, cellCenterZ);

                    ClassifyCell(
                        rayStart,
                        rayLength,
                        settings,
                        cosMaxSlope,
                        out var flag,
                        out var height);

                    flags[idx] = flag;
                    heights[idx] = height;
                    if ((flag & GeoDataFileWriter.FlagWalkable) != 0) walkableCount++;
                    if ((flag & GeoDataFileWriter.FlagBlocked) != 0) blockedCount++;
                }

                if (onProgress != null && !onProgress((cz + 1) / (float)depth))
                {
                    error = "Bake cancelled.";
                    return false;
                }
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var fs = File.Create(outputPath))
                GeoDataFileWriter.WriteGrid(fs, origin, settings.CellSize, width, depth, settings.MaxStep, flags, heights);

            result = new Result(width, depth, walkableCount, blockedCount);
            return true;
        }

        /// <summary>
        /// Classifies one cell by casting a single downward ray. Obstacle layers win over walkable layers when both
        /// are hit closest; a too-steep walkable surface is downgraded to blocked. Nothing hit → empty (flag 0).
        /// </summary>
        private static void ClassifyCell(
            Vector3 rayStart,
            float rayLength,
            Settings settings,
            float cosMaxSlope,
            out byte flag,
            out float height)
        {
            flag = 0;
            height = 0f;

            var combinedMask = settings.WalkableMask.value | settings.ObstacleMask.value;

            if (!Physics.Raycast(rayStart, Vector3.down, out var hit, rayLength, combinedMask, QueryTriggerInteraction.Ignore))
                return; // empty cell — no ground and no obstacle under it.

            var hitLayerBit = 1 << hit.collider.gameObject.layer;

            // Obstacle takes precedence: if the closest surface is on an obstacle layer, the cell is blocked.
            if ((settings.ObstacleMask.value & hitLayerBit) != 0)
            {
                flag = GeoDataFileWriter.FlagBlocked;
                height = hit.point.y;
                return;
            }

            if ((settings.WalkableMask.value & hitLayerBit) != 0)
            {
                // Steep ground counts as a wall.
                if (Vector3.Dot(hit.normal, Vector3.up) < cosMaxSlope)
                {
                    flag = GeoDataFileWriter.FlagBlocked;
                    height = hit.point.y;
                    return;
                }

                flag = GeoDataFileWriter.FlagWalkable;
                height = hit.point.y;
            }
        }

        /// <summary>
        /// Computes world bounds enclosing every active <see cref="Renderer"/> in the loaded scene(s). Returns
        /// <c>false</c> if the scene has no renderers.
        /// </summary>
        public static bool TryComputeSceneBounds(out Bounds bounds)
        {
#if UNITY_2023_1_OR_NEWER
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
#else
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            bounds = default;
            var found = false;
            foreach (var r in renderers)
            {
                if (!r.enabled) continue;
                if (!found)
                {
                    bounds = r.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            return found;
        }
    }
}
