using UnityEditor;
using UnityEngine;

namespace SetNet.GeoData.Unity.Editor
{
    /// <summary>
    /// Editor window (menu <c>SetNet &gt; Bake GeoData</c>) that bakes a portable SetNet GeoData file from the
    /// current scene, either from a baked NavMesh or by raycasting scene colliders into a walkability grid. The
    /// output file is loaded on a headless SetNet server via <c>GeoDataFile.LoadFromFile(...)</c> — no runtime
    /// engine dependency.
    /// </summary>
    public sealed class GeoDataBakerWindow : EditorWindow
    {
        private enum Mode
        {
            NavMesh = 0,
            Colliders = 1,
            LayeredColliders = 2,   // multi-storey grid
            Sectors = 3,            // tile the world into many geodatas + a manifest
        }

        // --- shared ---
        private Mode _mode = Mode.NavMesh;
        private string _outputPath = "Assets/world.geo";
        private string _lastSummary;
        private MessageType _lastSummaryType = MessageType.Info;
        private string _lastVisualizePath;   // set after a successful bake so we can add a visualizer
        private bool _lastVisualizeIsManifest;

        // --- collider-grid settings ---
        private float _cellSize = 1f;
        private LayerMask _walkableMask = ~0;   // Everything by default.
        private LayerMask _obstacleMask = 0;
        private float _maxSlopeDegrees = 45f;
        private float _maxStep = 1f;
        private bool _autoBounds = true;
        private Bounds _manualBounds = new Bounds(Vector3.zero, new Vector3(100f, 20f, 100f));

        // --- layered-grid extra settings ---
        private float _layerMatchTolerance = 2f;
        private float _layerMergeEpsilon = 0.3f;

        // --- sector settings ---
        private float _sectorSize = 64f;
        private bool _sectorLayered = true;

        [MenuItem("SetNet/Bake GeoData")]
        private static void Open()
        {
            var window = GetWindow<GeoDataBakerWindow>(false, "Bake GeoData", true);
            window.minSize = new Vector2(360f, 360f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("SetNet GeoData Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bakes a portable .geo file (SetNet.GeoData format). Load it on a headless server with " +
                "GeoDataFile.LoadFromFile(path). The written file has no runtime engine dependency.",
                MessageType.None);

            EditorGUILayout.Space();
            _mode = (Mode)EditorGUILayout.EnumPopup("Mode", _mode);

            EditorGUILayout.Space();
            DrawOutputPathField();

            EditorGUILayout.Space();
            switch (_mode)
            {
                case Mode.NavMesh:
                    DrawNavMeshMode();
                    break;
                case Mode.Colliders:
                    DrawColliderMode();
                    break;
                case Mode.LayeredColliders:
                    DrawLayeredMode();
                    break;
                case Mode.Sectors:
                    DrawSectorMode();
                    break;
            }

            if (!string.IsNullOrEmpty(_lastSummary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_lastSummary, _lastSummaryType);
            }

            if (!string.IsNullOrEmpty(_lastVisualizePath))
            {
                if (GUILayout.Button("Add Debug Visualizer to Scene"))
                    AddVisualizer(_lastVisualizePath, _lastVisualizeIsManifest);
            }
        }

        private void DrawOutputPathField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputPath = EditorGUILayout.TextField("Output Path", _outputPath);
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    var picked = EditorUtility.SaveFilePanel(
                        "Save GeoData File",
                        System.IO.Path.GetDirectoryName(ToAbsolute(_outputPath)),
                        System.IO.Path.GetFileNameWithoutExtension(_outputPath),
                        "geo");
                    if (!string.IsNullOrEmpty(picked))
                        _outputPath = ToProjectRelativeIfPossible(picked);
                }
            }
        }

        private void DrawNavMeshMode()
        {
            EditorGUILayout.HelpBox(
                "Writes a nav-mesh GeoData file from the scene's baked NavMesh (NavMesh.CalculateTriangulation). " +
                "Bake a NavMesh first (Window > AI > Navigation, or a NavMeshSurface).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputPath)))
            {
                if (GUILayout.Button("Bake From NavMesh", GUILayout.Height(30f)))
                    BakeNavMesh();
            }
        }

        private void DrawColliderMode()
        {
            EditorGUILayout.HelpBox(
                "Casts a downward ray over each grid cell: a walkable-layer hit becomes a walkable cell at the hit " +
                "height; an obstacle-layer hit (or a too-steep surface) becomes a blocked cell; nothing means empty. " +
                "Single-surface — for floors/bridges use the Layered mode.",
                MessageType.Info);

            DrawColliderSettings();
            DrawBoundsSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputPath)))
            {
                if (GUILayout.Button("Bake From Colliders", GUILayout.Height(30f)))
                    BakeColliders();
            }
        }

        private int EstimateCells(Bounds b)
        {
            var w = Mathf.Max(1, Mathf.CeilToInt(b.size.x / _cellSize));
            var d = Mathf.Max(1, Mathf.CeilToInt(b.size.z / _cellSize));
            return w * d;
        }

        private void BakeNavMesh()
        {
            var abs = ToAbsolute(_outputPath);
            if (NavMeshGeoBaker.Bake(abs, out var result, out var error))
            {
                RefreshIfInAssets();
                _lastVisualizePath = _outputPath; _lastVisualizeIsManifest = false;
                ShowSummary(
                    $"Baked nav-mesh → {_outputPath}\n{result.VertexCount} vertices, {result.TriangleCount} triangles.",
                    MessageType.Info);
            }
            else
            {
                ShowSummary(error, MessageType.Error);
            }
        }

        private void BakeColliders()
        {
            if (!ResolveBounds(out var bounds)) return;

            var settings = new ColliderGridBaker.Settings
            {
                CellSize = _cellSize,
                WalkableMask = _walkableMask,
                ObstacleMask = _obstacleMask,
                WorldBounds = bounds,
                MaxSlopeDegrees = _maxSlopeDegrees,
                MaxStep = _maxStep,
            };

            var abs = ToAbsolute(_outputPath);
            var ok = false;
            ColliderGridBaker.Result result = default;
            string error = null;
            try
            {
                ok = ColliderGridBaker.Bake(
                    abs,
                    settings,
                    progress =>
                    {
                        // Returns false when the user hits Cancel, which aborts the bake.
                        return !EditorUtility.DisplayCancelableProgressBar("Baking GeoData", "Raycasting cells...", progress);
                    },
                    out result,
                    out error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (ok)
            {
                RefreshIfInAssets();
                _lastVisualizePath = _outputPath; _lastVisualizeIsManifest = false;
                var empty = result.TotalCells - result.WalkableCells - result.BlockedCells;
                ShowSummary(
                    $"Baked grid → {_outputPath}\n{result.Width}x{result.Depth} = {result.TotalCells} cells " +
                    $"({result.WalkableCells} walkable, {result.BlockedCells} blocked, {empty} empty).",
                    MessageType.Info);
            }
            else
            {
                ShowSummary(error, MessageType.Error);
            }
        }

        // --- layered (multi-storey) mode ---

        private void DrawLayeredMode()
        {
            EditorGUILayout.HelpBox(
                "Multi-storey grid: casts a STACK of downward rays per cell and keeps EVERY walkable surface as a " +
                "separate height layer — so floors, bridges and overpasses bake without a nav-mesh. Loads on the " +
                "server as LayeredGridGeoData.",
                MessageType.Info);

            DrawColliderSettings();
            _layerMatchTolerance = Mathf.Max(0.01f, EditorGUILayout.FloatField("Layer Match Tolerance", _layerMatchTolerance));
            _layerMergeEpsilon = Mathf.Max(0.001f, EditorGUILayout.FloatField("Layer Merge Epsilon", _layerMergeEpsilon));

            DrawBoundsSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputPath)))
                if (GUILayout.Button("Bake Layered (Multi-Storey) Grid", GUILayout.Height(30f)))
                    BakeLayered();
        }

        private void BakeLayered()
        {
            if (!ResolveBounds(out var bounds)) return;
            var settings = new ColliderLayeredGridBaker.Settings
            {
                CellSize = _cellSize,
                WalkableMask = _walkableMask,
                ObstacleMask = _obstacleMask,
                WorldBounds = bounds,
                MaxSlopeDegrees = _maxSlopeDegrees,
                MaxStep = _maxStep,
                LayerMatchTolerance = _layerMatchTolerance,
                LayerMergeEpsilon = _layerMergeEpsilon,
            };

            var abs = ToAbsolute(_outputPath);
            var ok = false;
            ColliderLayeredGridBaker.Result result = default;
            string error = null;
            try
            {
                ok = ColliderLayeredGridBaker.Bake(abs, settings,
                    progress => !EditorUtility.DisplayCancelableProgressBar("Baking Layered GeoData", "Sweeping surfaces...", progress),
                    out result, out error);
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (ok)
            {
                RefreshIfInAssets();
                _lastVisualizePath = _outputPath; _lastVisualizeIsManifest = false;
                ShowSummary(
                    $"Baked layered grid → {_outputPath}\n{result.Width}x{result.Depth} cells, " +
                    $"{result.LayerCount} walkable layers, {result.WallCells} wall cells.",
                    MessageType.Info);
            }
            else ShowSummary(error, MessageType.Error);
        }

        // --- sectored mode ---

        private void DrawSectorMode()
        {
            EditorGUILayout.HelpBox(
                "Tiles the world into square sectors and bakes each into its own .geo, plus a .geomap manifest. The " +
                "server loads the whole world seamlessly with GeoDataManifest.Load(manifest) → one SectoredGeoData. " +
                "Use for large, zone-divided worlds. Output path's name is used as the manifest/sector base name.",
                MessageType.Info);

            _sectorSize = Mathf.Max(1f, EditorGUILayout.FloatField("Sector Size (world units)", _sectorSize));
            _sectorLayered = EditorGUILayout.Toggle("Layered (multi-storey) sectors", _sectorLayered);

            DrawColliderSettings();
            if (_sectorLayered)
            {
                _layerMatchTolerance = Mathf.Max(0.01f, EditorGUILayout.FloatField("Layer Match Tolerance", _layerMatchTolerance));
                _layerMergeEpsilon = Mathf.Max(0.001f, EditorGUILayout.FloatField("Layer Merge Epsilon", _layerMergeEpsilon));
            }

            DrawBoundsSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_outputPath)))
                if (GUILayout.Button("Bake Sectors + Manifest", GUILayout.Height(30f)))
                    BakeSectors();
        }

        private void BakeSectors()
        {
            if (!ResolveBounds(out var bounds)) return;
            var settings = new SectorBaker.Settings
            {
                SectorSize = _sectorSize,
                Layered = _sectorLayered,
                CellSize = _cellSize,
                WalkableMask = _walkableMask,
                ObstacleMask = _obstacleMask,
                MaxSlopeDegrees = _maxSlopeDegrees,
                MaxStep = _maxStep,
                LayerMatchTolerance = _layerMatchTolerance,
                LayerMergeEpsilon = _layerMergeEpsilon,
                WorldBounds = bounds,
            };

            var abs = ToAbsolute(_outputPath);
            var ok = false;
            SectorBaker.Result result = default;
            string error = null;
            try
            {
                ok = SectorBaker.Bake(abs, settings,
                    progress => !EditorUtility.DisplayCancelableProgressBar("Baking Sectors", "Baking sector tiles...", progress),
                    out result, out error);
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (ok)
            {
                RefreshIfInAssets();
                // The manifest sits next to the sectors; visualize the whole world through it.
                _lastVisualizePath = ToProjectRelativeIfPossible(result.ManifestPath);
                _lastVisualizeIsManifest = true;
                ShowSummary(
                    $"Baked {result.SectorsBaked} sectors ({result.SectorsX}x{result.SectorsZ}) + manifest → {_lastVisualizePath}\n" +
                    $"Load on the server with GeoDataManifest.Load(\"{System.IO.Path.GetFileName(result.ManifestPath)}\").",
                    MessageType.Info);
            }
            else ShowSummary(error, MessageType.Error);
        }

        // --- shared collider/bounds GUI + resolution ---

        private void DrawColliderSettings()
        {
            _cellSize = Mathf.Max(0.01f, EditorGUILayout.FloatField("Cell Size", _cellSize));
            _walkableMask = LayerMaskField("Walkable Layers", _walkableMask);
            _obstacleMask = LayerMaskField("Obstacle Layers", _obstacleMask);
            _maxSlopeDegrees = EditorGUILayout.Slider("Max Slope (deg)", _maxSlopeDegrees, 0f, 89f);
            _maxStep = Mathf.Max(0f, EditorGUILayout.FloatField("Max Step", _maxStep));
        }

        private void DrawBoundsSection()
        {
            EditorGUILayout.Space();
            _autoBounds = EditorGUILayout.Toggle("Auto Bounds (scene renderers)", _autoBounds);
            using (new EditorGUI.DisabledScope(_autoBounds))
            {
                _manualBounds.center = EditorGUILayout.Vector3Field("Bounds Center", _manualBounds.center);
                _manualBounds.size = EditorGUILayout.Vector3Field("Bounds Size", _manualBounds.size);
            }
            if (_autoBounds && GUILayout.Button("Preview Auto Bounds"))
            {
                if (ColliderGridBaker.TryComputeSceneBounds(out var b))
                    ShowSummary($"Auto bounds: center {b.center}, size {b.size} → ~{EstimateCells(b)} cells.", MessageType.Info);
                else
                    ShowSummary("No renderers in the scene to auto-fit bounds. Use manual bounds.", MessageType.Warning);
            }
        }

        private bool ResolveBounds(out Bounds bounds)
        {
            if (_autoBounds)
            {
                if (!ColliderGridBaker.TryComputeSceneBounds(out bounds))
                {
                    ShowSummary("No renderers in the scene to auto-fit bounds. Disable Auto Bounds and set them manually.", MessageType.Error);
                    return false;
                }
                return true;
            }
            bounds = _manualBounds;
            return true;
        }

        // Creates a GameObject with a GeoDataGizmo pointing at the just-baked file/manifest so the bake is visible.
        private void AddVisualizer(string path, bool isManifest)
        {
            var go = new GameObject(isManifest ? "GeoData Visualizer (sectors)" : "GeoData Visualizer");
            var gizmo = go.AddComponent<SetNet.GeoData.Unity.GeoDataGizmo>();
            gizmo.filePath = path;
            gizmo.isManifest = isManifest;
            Undo.RegisterCreatedObjectUndo(go, "Add GeoData Visualizer");
            Selection.activeGameObject = go;
            ShowSummary($"Added a GeoData Visualizer for '{path}'. Select it and look in the Scene view.", MessageType.Info);
        }

        private void ShowSummary(string message, MessageType type)
        {
            _lastSummary = message;
            _lastSummaryType = type;
            if (type == MessageType.Error) Debug.LogError("[GeoData Baker] " + message);
            else Debug.Log("[GeoData Baker] " + message);
            Repaint();
        }

        // --- path helpers ---

        /// <summary>Resolves a project-relative "Assets/..." path (or an already-absolute path) to an absolute path.</summary>
        private static string ToAbsolute(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (System.IO.Path.IsPathRooted(path)) return path;
            if (path.Replace('\\', '/').StartsWith("Assets/") || path == "Assets")
            {
                var projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? "";
                return System.IO.Path.Combine(projectRoot, path);
            }
            // Treat other relative paths as relative to the project root.
            var root = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return System.IO.Path.Combine(root, path);
        }

        /// <summary>Converts an absolute path under the project's Assets folder back to a "Assets/..." path when possible.</summary>
        private static string ToProjectRelativeIfPossible(string absolute)
        {
            var dataPath = Application.dataPath.Replace('\\', '/');
            var normalized = absolute.Replace('\\', '/');
            if (normalized.StartsWith(dataPath))
                return "Assets" + normalized.Substring(dataPath.Length);
            return absolute;
        }

        /// <summary>Re-imports the asset database if the output landed inside the project's Assets folder.</summary>
        private void RefreshIfInAssets()
        {
            var normalized = _outputPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/") || normalized == "Assets" ||
                ToAbsolute(_outputPath).Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/')))
            {
                AssetDatabase.Refresh();
            }
        }

        // --- GUI helpers ---

        /// <summary>A LayerMask field that behaves like Unity's built-in inspector mask (named layers only).</summary>
        private static LayerMask LayerMaskField(string label, LayerMask mask)
        {
            var layers = UnityEditorInternal.InternalEditorUtility.layers;
            var current = 0;
            for (var i = 0; i < layers.Length; i++)
            {
                var layerIndex = LayerMask.NameToLayer(layers[i]);
                if ((mask.value & (1 << layerIndex)) != 0)
                    current |= 1 << i;
            }

            var picked = EditorGUILayout.MaskField(label, current, layers);

            var result = 0;
            for (var i = 0; i < layers.Length; i++)
            {
                if ((picked & (1 << i)) != 0)
                    result |= 1 << LayerMask.NameToLayer(layers[i]);
            }
            return result;
        }
    }
}
