using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// One-click sectored bake: tiles the world into square sectors and bakes each into its own <c>.geo</c> file (grid
    /// or multi-storey layered grid), then writes a <c>.geomap</c> manifest tying them together. A headless server
    /// loads the whole world seamlessly with <c>GeoDataManifest.Load(manifest)</c> → a single <c>SectoredGeoData</c>.
    /// This is the automated path for large, zone-divided worlds — no per-sector manual setup.
    /// </summary>
    public static class SectorBaker
    {
        /// <summary>Inputs for a sectored bake (a superset of the single-file baker settings + the sector size).</summary>
        public struct Settings
        {
            /// <summary>Edge length of each square sector tile in world units.</summary>
            public float SectorSize;
            /// <summary>Bake each sector as a multi-storey layered grid (true) or a flat 2.5D grid (false).</summary>
            public bool Layered;

            /// <summary>Cell edge length within a sector.</summary>
            public float CellSize;
            /// <summary>Walkable collider layers.</summary>
            public LayerMask WalkableMask;
            /// <summary>Obstacle collider layers.</summary>
            public LayerMask ObstacleMask;
            /// <summary>Max walkable slope (degrees).</summary>
            public float MaxSlopeDegrees;
            /// <summary>Max climbable step between adjacent cells/layers.</summary>
            public float MaxStep;
            /// <summary>(Layered only) how close a query Y must be to a layer to stand on it.</summary>
            public float LayerMatchTolerance;
            /// <summary>(Layered only) merge surfaces closer than this in Y into one layer.</summary>
            public float LayerMergeEpsilon;
            /// <summary>The whole world's bounds to tile.</summary>
            public Bounds WorldBounds;
        }

        /// <summary>Summary of a sectored bake.</summary>
        public readonly struct Result
        {
            /// <summary>Sector tiles along X.</summary>
            public readonly int SectorsX;
            /// <summary>Sector tiles along Z.</summary>
            public readonly int SectorsZ;
            /// <summary>Sectors actually written.</summary>
            public readonly int SectorsBaked;
            /// <summary>The manifest file path.</summary>
            public readonly string ManifestPath;

            /// <summary>Creates a result.</summary>
            public Result(int sectorsX, int sectorsZ, int sectorsBaked, string manifestPath)
            {
                SectorsX = sectorsX; SectorsZ = sectorsZ; SectorsBaked = sectorsBaked; ManifestPath = manifestPath;
            }
        }

        /// <summary>
        /// Tiles <paramref name="settings"/>.WorldBounds into sectors, bakes each next to <paramref name="baseOutputPath"/>
        /// as <c>&lt;name&gt;_x{i}_z{j}.geo</c>, and writes <c>&lt;name&gt;.geomap</c>. <paramref name="onProgress"/>
        /// (0..1) is called per sector (return <c>false</c> to cancel).
        /// </summary>
        public static bool Bake(
            string baseOutputPath,
            Settings settings,
            Func<float, bool> onProgress,
            out Result result,
            out string error)
        {
            result = default;
            error = null;

            if (settings.SectorSize <= 0f) { error = "Sector size must be greater than zero."; return false; }
            if (settings.CellSize <= 0f) { error = "Cell size must be greater than zero."; return false; }
            var size = settings.WorldBounds.size;
            if (size.x <= 0f || size.z <= 0f)
            {
                error = "World bounds are empty on the XZ plane. Set explicit bounds or ensure the scene has renderers to auto-fit.";
                return false;
            }

            var nx = Mathf.Max(1, Mathf.CeilToInt(size.x / settings.SectorSize));
            var nz = Mathf.Max(1, Mathf.CeilToInt(size.z / settings.SectorSize));
            var total = nx * nz;

            var dir = Path.GetDirectoryName(baseOutputPath) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(baseOutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var entries = new List<GeoDataManifestWriter.Entry>(total);
            var origin = settings.WorldBounds.min;
            var maxXZ = settings.WorldBounds.max;
            var baked = 0;

            for (var iz = 0; iz < nz; iz++)
            {
                for (var ix = 0; ix < nx; ix++)
                {
                    var tileMinX = origin.x + ix * settings.SectorSize;
                    var tileMinZ = origin.z + iz * settings.SectorSize;
                    var tileMaxX = Mathf.Min(tileMinX + settings.SectorSize, maxXZ.x);
                    var tileMaxZ = Mathf.Min(tileMinZ + settings.SectorSize, maxXZ.z);

                    var tileBounds = new Bounds();
                    tileBounds.SetMinMax(
                        new Vector3(tileMinX, origin.y, tileMinZ),
                        new Vector3(tileMaxX, maxXZ.y, tileMaxZ));

                    var id = $"x{ix}_z{iz}";
                    var fileName = $"{baseName}_{id}.geo";
                    var filePath = Path.Combine(dir, fileName);

                    bool ok;
                    string subError;
                    if (settings.Layered)
                    {
                        ok = ColliderLayeredGridBaker.Bake(filePath, new ColliderLayeredGridBaker.Settings
                        {
                            CellSize = settings.CellSize,
                            WalkableMask = settings.WalkableMask,
                            ObstacleMask = settings.ObstacleMask,
                            WorldBounds = tileBounds,
                            MaxSlopeDegrees = settings.MaxSlopeDegrees,
                            MaxStep = settings.MaxStep,
                            LayerMatchTolerance = settings.LayerMatchTolerance,
                            LayerMergeEpsilon = settings.LayerMergeEpsilon,
                        }, null, out _, out subError);
                    }
                    else
                    {
                        ok = ColliderGridBaker.Bake(filePath, new ColliderGridBaker.Settings
                        {
                            CellSize = settings.CellSize,
                            WalkableMask = settings.WalkableMask,
                            ObstacleMask = settings.ObstacleMask,
                            WorldBounds = tileBounds,
                            MaxSlopeDegrees = settings.MaxSlopeDegrees,
                            MaxStep = settings.MaxStep,
                        }, null, out _, out subError);
                    }

                    if (!ok) { error = $"Sector {id}: {subError}"; return false; }

                    entries.Add(new GeoDataManifestWriter.Entry
                    {
                        Id = id,
                        RelativePath = fileName,   // relative to the manifest's own folder
                        Bounds = tileBounds,
                    });
                    baked++;

                    if (onProgress != null && !onProgress(baked / (float)total))
                    {
                        error = "Bake cancelled.";
                        return false;
                    }
                }
            }

            var manifestPath = Path.Combine(dir, baseName + ".geomap");
            GeoDataManifestWriter.WriteToFile(manifestPath, entries);

            result = new Result(nx, nz, baked, manifestPath);
            return true;
        }
    }
}
