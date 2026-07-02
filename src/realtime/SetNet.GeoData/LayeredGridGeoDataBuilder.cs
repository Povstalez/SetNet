using System;
using System.Collections.Generic;

namespace SetNet.GeoData
{
    /// <summary>
    /// Builds a <see cref="LayeredGridGeoData"/>. Add one or more walkable height layers per cell (ground floor, the
    /// floor above it, a bridge deck…), or mark a cell a full-height wall. A baker (e.g. a Unity collider sweep that
    /// finds every walkable surface at each XZ) drives this — one <see cref="AddLayer"/> per surface found — or fill it
    /// by hand for tests. Layers are sorted by height at <see cref="Build"/>.
    /// </summary>
    public sealed class LayeredGridGeoDataBuilder
    {
        private readonly Vec3 _origin;
        private readonly float _cell;
        private readonly int _w, _d;
        private readonly List<(float y, bool walkable)>?[] _cells;
        private readonly bool[] _wall;
        private float _maxStep = 1f;
        private float _matchTol = 2f;

        /// <summary>Creates a builder for a <paramref name="width"/>×<paramref name="depth"/> grid of <paramref name="cellSize"/>-sized cells whose (0,0) corner is at <paramref name="origin"/>. All cells start empty (no layers).</summary>
        public LayeredGridGeoDataBuilder(Vec3 origin, float cellSize, int width, int depth)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
            _origin = origin; _cell = cellSize; _w = width; _d = depth;
            _cells = new List<(float, bool)>?[width * depth];
            _wall = new bool[width * depth];
        }

        /// <summary>Adds a walkable surface at world height <paramref name="height"/> to cell (<paramref name="cx"/>,<paramref name="cz"/>). Call it once per floor that overlaps this cell.</summary>
        public LayeredGridGeoDataBuilder AddLayer(int cx, int cz, float height, bool walkable = true)
        {
            if (InBounds(cx, cz))
            {
                var i = cz * _w + cx;
                _wall[i] = false;
                (_cells[i] ??= new List<(float, bool)>()).Add((height, walkable));
            }
            return this;
        }

        /// <summary>Marks a cell a full-height wall — blocks movement and sight at every height (clears its layers).</summary>
        public LayeredGridGeoDataBuilder SetWall(int cx, int cz)
        {
            if (InBounds(cx, cz)) { var i = cz * _w + cx; _wall[i] = true; _cells[i] = null; }
            return this;
        }

        /// <summary>Sets the maximum climbable height step between adjacent cell layers (default 1).</summary>
        public LayeredGridGeoDataBuilder SetMaxStep(float maxStep) { _maxStep = maxStep; return this; }

        /// <summary>Sets how close (world Y) a query point must be to a layer to count as standing on it (default 2).</summary>
        public LayeredGridGeoDataBuilder SetLayerMatchTolerance(float tolerance) { _matchTol = tolerance; return this; }

        /// <summary>Builds the immutable layered grid (CSR-packs the layers, sorted ascending per cell).</summary>
        public LayeredGridGeoData Build()
        {
            var n = _w * _d;
            var cellStart = new int[n + 1];
            var total = 0;
            for (var i = 0; i < n; i++) total += _cells[i]?.Count ?? 0;

            var layerY = new float[total];
            var layerWalk = new byte[total];
            var layerCell = new int[total];
            var k = 0;
            for (var i = 0; i < n; i++)
            {
                cellStart[i] = k;
                var list = _cells[i];
                if (list != null)
                {
                    list.Sort((a, b) => a.y.CompareTo(b.y));   // ascending by height
                    foreach (var (y, walk) in list)
                    {
                        layerY[k] = y; layerWalk[k] = walk ? (byte)1 : (byte)0; layerCell[k] = i; k++;
                    }
                }
            }
            cellStart[n] = k;

            return new LayeredGridGeoData(_origin, _cell, _w, _d, _maxStep, _matchTol,
                cellStart, layerY, layerWalk, layerCell, _wall);
        }

        private bool InBounds(int cx, int cz) => cx >= 0 && cx < _w && cz >= 0 && cz < _d;
    }
}
