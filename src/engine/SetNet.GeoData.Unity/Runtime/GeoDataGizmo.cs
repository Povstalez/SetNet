using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SetNet.GeoData.Unity
{
    /// <summary>
    /// Drops into a scene to <b>visualize a baked geodata</b> in the Scene view — so you can see exactly what the baker
    /// produced: which cells are walkable, where the walls are, the stacked layers of a multi-storey grid, the nav-mesh
    /// triangles, and (for a sectored world) each sector's footprint. Point it at a single <c>.geo</c> file (as a path
    /// or a <c>TextAsset</c>) or at a <c>.geomap</c> manifest to draw the whole sectored world at once. Editor-only:
    /// it draws with Gizmos; it does nothing at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GeoDataGizmo : MonoBehaviour
    {
        [Header("Source (use ONE)")]
        [Tooltip("A .geo (or .geomap) file path, project-relative (Assets/...) or absolute. Read in the editor.")]
        public string filePath = "Assets/world.geo";
        [Tooltip("Optional: a .geo imported as a TextAsset (rename to .bytes). Takes priority over filePath when set.")]
        public TextAsset geoAsset;
        [Tooltip("Treat the source as a .geomap manifest and draw every sector it references.")]
        public bool isManifest;

        [Header("What to draw")]
        public bool drawWalkable = true;
        public bool drawBlocked = true;
        public bool drawWalls = true;
        public bool drawNavMesh = true;
        public bool drawSectorBounds = true;
        [Tooltip("Also draw when this object is NOT selected.")]
        public bool showWhenDeselected = true;
        [Tooltip("Safety cap on how many cells/triangles to draw (subsampled beyond this).")]
        public int maxDrawn = 20000;

        [Header("Colors")]
        public Color walkableColor = new Color(0.25f, 0.9f, 0.35f, 0.6f);
        public Color blockedColor = new Color(0.9f, 0.25f, 0.2f, 0.6f);
        public Color wallColor = new Color(0.85f, 0.15f, 0.15f, 0.5f);
        public Color navMeshColor = new Color(0.2f, 0.8f, 0.95f, 0.9f);
        public Color sectorColor = new Color(0.95f, 0.85f, 0.2f, 0.9f);

        // Cache: only re-read the file(s) when the source identity changes (or via the Reload context menu).
        private string _loadedKey;
        private ParsedGeoData _cached;
        private readonly List<(GeoManifestEntry entry, ParsedGeoData geo)> _cachedSectors = new List<(GeoManifestEntry, ParsedGeoData)>();

        /// <summary>Forces the file(s) to be re-read on the next draw (use after re-baking).</summary>
        [ContextMenu("Reload GeoData")]
        public void Reload()
        {
            _loadedKey = null;
            _cached = null;
            _cachedSectors.Clear();
        }

        private void OnDrawGizmos() { if (showWhenDeselected) Draw(); }
        private void OnDrawGizmosSelected() { if (!showWhenDeselected) Draw(); }

        private void Draw()
        {
            EnsureLoaded();
            if (isManifest)
            {
                foreach (var (entry, geo) in _cachedSectors)
                {
                    if (drawSectorBounds)
                    {
                        Gizmos.color = sectorColor;
                        Gizmos.DrawWireCube(entry.Bounds.center, entry.Bounds.size);
                    }
                    if (geo != null) DrawParsed(geo);
                }
            }
            else if (_cached != null)
            {
                DrawParsed(_cached);
            }
        }

        private void EnsureLoaded()
        {
            var key = geoAsset != null ? "asset:" + geoAsset.GetInstanceID() : "path:" + filePath + "|manifest:" + isManifest;
            if (key == _loadedKey) return;
            _loadedKey = key;
            _cached = null;
            _cachedSectors.Clear();

            try
            {
                if (isManifest)
                {
                    var manifestPath = ResolvePath(filePath);
                    var dir = Path.GetDirectoryName(manifestPath) ?? "";
                    foreach (var e in GeoDataManifestReader.ReadFile(manifestPath))
                    {
                        ParsedGeoData geo = null;
                        var geoPath = Path.IsPathRooted(e.RelativePath) ? e.RelativePath : Path.Combine(dir, e.RelativePath);
                        if (File.Exists(geoPath)) geo = GeoDataFileReader.ReadFile(geoPath);
                        _cachedSectors.Add((e, geo));
                    }
                }
                else if (geoAsset != null)
                {
                    _cached = GeoDataFileReader.Read(geoAsset.bytes);
                }
                else
                {
                    _cached = GeoDataFileReader.ReadFile(ResolvePath(filePath));
                }
            }
            catch (System.Exception ex)
            {
                // Debug tool — never throw out of a gizmo callback; just log once per source change.
                Debug.LogWarning($"[GeoDataGizmo] Could not load '{filePath}': {ex.Message}", this);
            }
        }

        // Resolves an "Assets/..."-relative path to absolute (editor); leaves absolute paths as-is.
        private static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
            var norm = path.Replace('\\', '/');
            if (norm.StartsWith("Assets/") || norm == "Assets")
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                return Path.Combine(root, path);
            }
            return path;
        }

        private void DrawParsed(ParsedGeoData p)
        {
            switch (p.Kind)
            {
                case GeoKind.Grid: DrawGrid(p); break;
                case GeoKind.LayeredGrid: DrawLayered(p); break;
                case GeoKind.NavMesh: DrawNavMesh(p); break;
            }
        }

        private void DrawGrid(ParsedGeoData p)
        {
            var total = p.Width * p.Depth;
            var stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(total / (float)Mathf.Max(1, maxDrawn))));
            var quad = new Vector3(p.CellSize * 0.9f, 0.02f, p.CellSize * 0.9f);
            for (var cz = 0; cz < p.Depth; cz += stride)
                for (var cx = 0; cx < p.Width; cx += stride)
                {
                    var flag = p.Flags[cz * p.Width + cx];
                    var walkable = GeoDataFileReader.IsWalkable(flag);
                    var blocked = GeoDataFileReader.IsBlocked(flag);
                    if (walkable && !drawWalkable) continue;
                    if (blocked && !drawBlocked) continue;
                    if (!walkable && !blocked) continue;   // empty
                    Gizmos.color = walkable ? walkableColor : blockedColor;
                    var c = new Vector3(p.Origin.x + (cx + 0.5f) * p.CellSize, p.Heights[cz * p.Width + cx], p.Origin.z + (cz + 0.5f) * p.CellSize);
                    Gizmos.DrawCube(c, quad);
                }
        }

        private void DrawLayered(ParsedGeoData p)
        {
            var total = p.Width * p.Depth;
            var stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(total / (float)Mathf.Max(1, maxDrawn))));
            var quad = new Vector3(p.CellSize * 0.85f, 0.02f, p.CellSize * 0.85f);
            for (var cz = 0; cz < p.Depth; cz += stride)
                for (var cx = 0; cx < p.Width; cx += stride)
                {
                    var i = cz * p.Width + cx;
                    var cxW = p.Origin.x + (cx + 0.5f) * p.CellSize;
                    var czW = p.Origin.z + (cz + 0.5f) * p.CellSize;
                    if (p.Walls[i])
                    {
                        if (drawWalls)
                        {
                            Gizmos.color = wallColor;
                            Gizmos.DrawCube(new Vector3(cxW, p.Origin.y + 1f, czW), new Vector3(p.CellSize * 0.7f, 2f, p.CellSize * 0.7f));
                        }
                        continue;
                    }
                    if (!drawWalkable) continue;
                    for (var l = p.CellStart[i]; l < p.CellStart[i + 1]; l++)
                    {
                        // Tint each layer slightly by height so stacked floors read apart.
                        Gizmos.color = p.LayerWalkable[l] != 0 ? walkableColor : blockedColor;
                        Gizmos.DrawCube(new Vector3(cxW, p.LayerHeights[l], czW), quad);
                    }
                }
        }

        private void DrawNavMesh(ParsedGeoData p)
        {
            if (!drawNavMesh || p.Indices == null) return;
            Gizmos.color = navMeshColor;
            var triCount = p.Indices.Length / 3;
            var stride = Mathf.Max(1, Mathf.CeilToInt(triCount / (float)Mathf.Max(1, maxDrawn)));
            for (var t = 0; t < triCount; t += stride)
            {
                var a = p.Vertices[p.Indices[t * 3]];
                var b = p.Vertices[p.Indices[t * 3 + 1]];
                var c = p.Vertices[p.Indices[t * 3 + 2]];
                Gizmos.DrawLine(a, b);
                Gizmos.DrawLine(b, c);
                Gizmos.DrawLine(c, a);
            }
        }
    }
}
