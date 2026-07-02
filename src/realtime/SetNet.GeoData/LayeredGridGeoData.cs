using System;

namespace SetNet.GeoData
{
    /// <summary>
    /// A multi-layer navigation grid — the classic MMO-server "geodata" shape (as used by Lineage 2 and friends) for
    /// worlds with <b>floors, bridges, overpasses and overhangs</b> without a full nav-mesh. It is a
    /// <see cref="GridGeoData"/> that allows <i>several walkable surfaces stacked at the same XZ cell</i>: a cell holds
    /// zero or more <b>layers</b>, each a walkable height. Every query carries the agent's current Y and resolves to the
    /// layer nearest that height, so movement, height sampling and walkability are all storey-aware — an agent on the
    /// ground floor never accidentally snaps onto the bridge above it.
    /// </summary>
    /// <remarks>
    /// Storage is CSR-packed (one offset array + flat layer arrays) so it stays compact and cache-friendly for
    /// city-sized maps. Build one with <see cref="LayeredGridGeoDataBuilder"/> or load a baked <see cref="GeoDataFile"/>.
    /// <para><b>Line-of-sight</b> here is occluded by full-height <i>wall</i> cells and by any floor whose height lies
    /// strictly between the two endpoints and which the straight ray passes through (so you can't see a target on the
    /// storey above through the floor). It does not model arbitrary opaque ceilings/soffits — for volumetric precision
    /// use a <see cref="NavMeshGeoData"/>. Movement/height queries are fully layer-accurate.</para>
    /// </remarks>
    public sealed class LayeredGridGeoData : IGeoData
    {
        private readonly Vec3 _origin;
        private readonly float _cell;
        private readonly int _w, _d;
        private readonly float _maxStep;
        private readonly float _matchTol;

        private readonly int[] _cellStart;     // CSR: layers of cell i are [_cellStart[i], _cellStart[i+1])
        private readonly float[] _layerY;       // ascending within each cell
        private readonly byte[] _layerWalk;     // 1 = a surface you can stand on
        private readonly int[] _layerCell;      // layer -> owning cell index (for the pathfinder)
        private readonly bool[] _wall;          // full-height wall cell (blocks all heights + sight)
        private readonly float _minY, _maxY;

        /// <summary>The largest ground-height step an agent may traverse between adjacent cell layers. Default 1.</summary>
        public float MaxStep => _maxStep;
        /// <summary>How close (world Y) a query point must be to a layer to be considered standing on it. Default 2.</summary>
        public float LayerMatchTolerance => _matchTol;
        /// <summary>Cell edge length (world units).</summary>
        public float CellSize => _cell;
        /// <summary>Number of cells along X.</summary>
        public int Width => _w;
        /// <summary>Number of cells along Z.</summary>
        public int Depth => _d;
        /// <summary>The grid's min corner (cell 0,0 origin) on the XZ plane.</summary>
        public Vec3 Origin => _origin;
        /// <summary>Total number of layers across all cells (the pathfinder's node count).</summary>
        public int LayerCount => _layerY.Length;

        internal LayeredGridGeoData(Vec3 origin, float cell, int w, int d, float maxStep, float matchTol,
            int[] cellStart, float[] layerY, byte[] layerWalk, int[] layerCell, bool[] wall)
        {
            _origin = origin; _cell = cell; _w = w; _d = d; _maxStep = maxStep; _matchTol = matchTol;
            _cellStart = cellStart; _layerY = layerY; _layerWalk = layerWalk; _layerCell = layerCell; _wall = wall;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (var i = 0; i < layerY.Length; i++)
                if (layerWalk[i] != 0) { if (layerY[i] < min) min = layerY[i]; if (layerY[i] > max) max = layerY[i]; }
            _minY = float.IsInfinity(min) ? 0 : min;
            _maxY = float.IsInfinity(max) ? 0 : max;
        }

        /// <inheritdoc/>
        public Bounds Bounds => new Bounds(
            new Vec3(_origin.X, _minY, _origin.Z),
            new Vec3(_origin.X + _w * _cell, _maxY, _origin.Z + _d * _cell));

        /// <summary>Converts a world point to its cell indices; returns false if outside the grid.</summary>
        public bool WorldToCell(Vec3 p, out int cx, out int cz)
        {
            cx = (int)MathF.Floor((p.X - _origin.X) / _cell);
            cz = (int)MathF.Floor((p.Z - _origin.Z) / _cell);
            return cx >= 0 && cx < _w && cz >= 0 && cz < _d;
        }

        private int CellIndex(int cx, int cz) => cz * _w + cx;
        private bool InBounds(int cx, int cz) => cx >= 0 && cx < _w && cz >= 0 && cz < _d;

        // --- layer accessors used by the pathfinder ---

        /// <summary>The owning cell index of a global layer.</summary>
        public int LayerCellIndex(int layer) => _layerCell[layer];
        /// <summary>The world Y (ground height) of a global layer.</summary>
        public float LayerHeight(int layer) => _layerY[layer];
        /// <summary>The world-space centre of a global layer (cell centre on XZ, layer height on Y).</summary>
        public Vec3 LayerCenter(int layer)
        {
            var c = _layerCell[layer];
            int cx = c % _w, cz = c / _w;
            return new Vec3(_origin.X + (cx + 0.5f) * _cell, _layerY[layer], _origin.Z + (cz + 0.5f) * _cell);
        }

        /// <summary>
        /// Finds the walkable layer in cell (<paramref name="cx"/>,<paramref name="cz"/>) closest in height to
        /// <paramref name="y"/> and within <paramref name="maxDelta"/> of it. This is the storey-resolution primitive:
        /// pathfinding uses it with <see cref="MaxStep"/> to only step between vertically-adjacent surfaces.
        /// </summary>
        public bool TryLayerNear(int cx, int cz, float y, float maxDelta, out int layer)
        {
            layer = -1;
            if (!InBounds(cx, cz)) return false;
            var ci = CellIndex(cx, cz);
            if (_wall[ci]) return false;
            float best = float.PositiveInfinity;
            for (var l = _cellStart[ci]; l < _cellStart[ci + 1]; l++)
            {
                if (_layerWalk[l] == 0) continue;
                var dy = MathF.Abs(_layerY[l] - y);
                if (dy <= maxDelta && dy < best) { best = dy; layer = l; }
            }
            return layer >= 0;
        }

        // Nearest walkable layer to y in a cell regardless of distance (for height sampling / snapping).
        private bool TryNearestLayer(int cx, int cz, float y, out int layer, out float height)
        {
            layer = -1; height = float.NaN;
            if (!InBounds(cx, cz)) return false;
            var ci = CellIndex(cx, cz);
            if (_wall[ci]) return false;
            float best = float.PositiveInfinity;
            for (var l = _cellStart[ci]; l < _cellStart[ci + 1]; l++)
            {
                if (_layerWalk[l] == 0) continue;
                var dy = MathF.Abs(_layerY[l] - y);
                if (dy < best) { best = dy; layer = l; height = _layerY[l]; }
            }
            return layer >= 0;
        }

        /// <summary>Snaps a world point to the nearest walkable layer at (or spiralling out from) its cell, matching its Y.</summary>
        public bool TrySnapLayer(Vec3 p, out int layer)
        {
            WorldToCellClamped(p, out var cx, out var cz);
            if (TryNearestLayer(cx, cz, p.Y, out layer, out _)) return true;
            var maxR = Math.Max(_w, _d);
            for (var r = 1; r <= maxR; r++)
                for (var dz = -r; dz <= r; dz++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                        if (TryNearestLayer(cx + dx, cz + dz, p.Y, out layer, out _)) return true;
                    }
            layer = -1;
            return false;
        }

        /// <inheritdoc/>
        public bool IsWalkable(Vec3 point)
            => WorldToCell(point, out var cx, out var cz) && TryLayerNear(cx, cz, point.Y, _matchTol, out _);

        /// <inheritdoc/>
        public float SampleHeight(Vec3 point)
            => WorldToCell(point, out var cx, out var cz) && TryNearestLayer(cx, cz, point.Y, out _, out var h) ? h : float.NaN;

        /// <inheritdoc/>
        public Vec3 SampleNearestWalkable(Vec3 point)
            => TrySnapLayer(point, out var layer) ? LayerCenter(layer) : point;

        /// <inheritdoc/>
        public bool CanWalkStraight(Vec3 from, Vec3 to)
        {
            var steps = SampleCount(from, to);
            // Follow the floor: start on the layer nearest 'from', then require each next cell to have a layer within
            // one MaxStep of the height we're currently at — that's what stops "walking straight" up a cliff or onto the
            // storey above through the air, while letting a staircase (a run of small steps) through.
            if (!WorldToCell(from, out var fcx, out var fcz) || !TryNearestLayer(fcx, fcz, from.Y, out _, out var curH))
                return false;
            for (var i = 1; i <= steps; i++)
            {
                var p = Vec3.Lerp(from, to, (float)i / steps);
                if (!WorldToCell(p, out var cx, out var cz)) return false;
                if (!TryLayerNear(cx, cz, curH, _maxStep, out var layer)) return false;
                curH = _layerY[layer];
            }
            // Following the floor must actually land on the destination's storey — otherwise we merely reached the XZ
            // below/above the target (e.g. walked the ground floor to a spot under the upper deck), which is not a walk to it.
            return MathF.Abs(curH - to.Y) <= _maxStep;
        }

        /// <inheritdoc/>
        public bool LineOfSight(Vec3 from, Vec3 to)
        {
            var steps = SampleCount(from, to);
            var loY = MathF.Min(from.Y, to.Y);
            var hiY = MathF.Max(from.Y, to.Y);
            const float eps = 1e-3f;
            var prev = from;
            for (var i = 0; i <= steps; i++)
            {
                var p = Vec3.Lerp(from, to, steps == 0 ? 0 : (float)i / steps);
                WorldToCellClamped(p, out var cx, out var cz);
                var ci = CellIndex(cx, cz);
                if (_wall[ci]) return false;   // full-height wall blocks sight

                // A floor strictly between the two endpoints that the ray passes through occludes (can't see through a storey).
                if (i > 0)
                {
                    for (var l = _cellStart[ci]; l < _cellStart[ci + 1]; l++)
                    {
                        if (_layerWalk[l] == 0) continue;
                        var h = _layerY[l];
                        if (h <= loY + eps || h >= hiY - eps) continue;                 // not strictly between the endpoints' heights
                        if ((prev.Y - h) * (p.Y - h) <= 0f) return false;               // ray crosses this floor plane here
                    }
                }
                prev = p;
            }
            return true;
        }

        /// <inheritdoc/>
        public RaycastHit Raycast(Vec3 origin, Vec3 direction, float maxDistance)
        {
            var dir = direction.Normalized;
            if (dir.LengthSquared < 1e-9f) return RaycastHit.None;
            var step = _cell * 0.5f;
            var prev = origin;
            for (float t = step; t <= maxDistance; t += step)
            {
                var p = origin + dir * t;
                WorldToCellClamped(p, out var cx, out var cz);
                var ci = CellIndex(cx, cz);
                if (_wall[ci]) return new RaycastHit(true, p, t, (dir * -1f).Normalized);
                // Ground hit: the ray descends through a walkable floor plane in this cell.
                for (var l = _cellStart[ci]; l < _cellStart[ci + 1]; l++)
                {
                    if (_layerWalk[l] == 0) continue;
                    var h = _layerY[l];
                    if (prev.Y >= h && p.Y <= h) return new RaycastHit(true, new Vec3(p.X, h, p.Z), t, new Vec3(0, 1, 0));
                }
                prev = p;
            }
            return RaycastHit.None;
        }

        private int SampleCount(Vec3 from, Vec3 to)
        {
            var dist = Vec3.HorizontalDistance(from, to);
            return Math.Max(1, (int)MathF.Ceiling(dist / (_cell * 0.5f)));
        }

        private void WorldToCellClamped(Vec3 p, out int cx, out int cz)
        {
            cx = (int)MathF.Floor((p.X - _origin.X) / _cell);
            cz = (int)MathF.Floor((p.Z - _origin.Z) / _cell);
            if (cx < 0) cx = 0; else if (cx >= _w) cx = _w - 1;
            if (cz < 0) cz = 0; else if (cz >= _d) cz = _d - 1;
        }

        // --- accessors used by GeoDataFile serialization ---
        internal int[] CellStart => _cellStart;
        internal float[] LayerYs => _layerY;
        internal byte[] LayerWalks => _layerWalk;
        internal bool[] Walls => _wall;
    }
}
