using System;

namespace SetNet.GeoData
{
    /// <summary>
    /// A 2.5D navigation grid: a rectangular field of square cells over the XZ plane, each flagged walkable and/or
    /// blocked (a wall/obstacle) and carrying a ground height. Cheap to query and easy to bake automatically from
    /// scene colliders. Build one with <see cref="GridGeoDataBuilder"/> or load a baked <see cref="GeoDataFile"/>.
    /// </summary>
    public sealed class GridGeoData : IGeoData
    {
        internal const byte FlagWalkable = 1;
        internal const byte FlagBlocked = 2;

        private readonly Vec3 _origin;   // min corner (X,Z); Y is per-cell height
        private readonly float _cell;
        private readonly int _w, _d;
        private readonly byte[] _flags;
        private readonly float[] _height;
        private readonly float _minY, _maxY;

        /// <summary>The largest ground-height step an agent may traverse between adjacent cells (for can-walk-straight). Default 1.</summary>
        public float MaxStep { get; }

        /// <summary>Cell edge length (world units).</summary>
        public float CellSize => _cell;
        /// <summary>Number of cells along X.</summary>
        public int Width => _w;
        /// <summary>Number of cells along Z.</summary>
        public int Depth => _d;
        /// <summary>The grid's min corner (cell 0,0 origin) on the XZ plane.</summary>
        public Vec3 Origin => _origin;

        internal GridGeoData(Vec3 origin, float cell, int w, int d, byte[] flags, float[] height, float maxStep)
        {
            _origin = origin; _cell = cell; _w = w; _d = d; _flags = flags; _height = height; MaxStep = maxStep;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (var i = 0; i < flags.Length; i++)
                if ((flags[i] & FlagWalkable) != 0) { if (height[i] < min) min = height[i]; if (height[i] > max) max = height[i]; }
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

        /// <summary>The world-space centre of a cell (at its ground height).</summary>
        public Vec3 CellCenter(int cx, int cz)
            => new Vec3(_origin.X + (cx + 0.5f) * _cell, _height[cz * _w + cx], _origin.Z + (cz + 0.5f) * _cell);

        /// <summary>True if a cell (by index) is walkable and not blocked.</summary>
        public bool IsWalkableCell(int cx, int cz)
        {
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _d) return false;
            var f = _flags[cz * _w + cx];
            return (f & FlagWalkable) != 0 && (f & FlagBlocked) == 0;
        }

        private bool IsBlockedCell(int cx, int cz)
        {
            if (cx < 0 || cx >= _w || cz < 0 || cz >= _d) return true;   // outside = wall
            return (_flags[cz * _w + cx] & FlagBlocked) != 0;
        }

        /// <inheritdoc/>
        public bool IsWalkable(Vec3 point) => WorldToCell(point, out var cx, out var cz) && IsWalkableCell(cx, cz);

        /// <inheritdoc/>
        public float SampleHeight(Vec3 point)
            => WorldToCell(point, out var cx, out var cz) && IsWalkableCell(cx, cz) ? _height[cz * _w + cx] : float.NaN;

        /// <inheritdoc/>
        public bool LineOfSight(Vec3 from, Vec3 to)
        {
            // Sight is blocked by a wall cell anywhere along the XZ segment.
            var steps = SampleCount(from, to);
            for (var i = 0; i <= steps; i++)
            {
                var p = Vec3.Lerp(from, to, steps == 0 ? 0 : (float)i / steps);
                WorldToCellClamped(p, out var cx, out var cz);
                if (IsBlockedCell(cx, cz)) return false;
            }
            return true;
        }

        /// <inheritdoc/>
        public bool CanWalkStraight(Vec3 from, Vec3 to)
        {
            var steps = SampleCount(from, to);
            float prevH = float.NaN;
            for (var i = 0; i <= steps; i++)
            {
                var p = Vec3.Lerp(from, to, steps == 0 ? 0 : (float)i / steps);
                if (!WorldToCell(p, out var cx, out var cz) || !IsWalkableCell(cx, cz)) return false;
                var h = _height[cz * _w + cx];
                if (!float.IsNaN(prevH) && MathF.Abs(h - prevH) > MaxStep) return false;   // too steep a step
                prevH = h;
            }
            return true;
        }

        /// <inheritdoc/>
        public RaycastHit Raycast(Vec3 origin, Vec3 direction, float maxDistance)
        {
            var dir = direction.Normalized;
            if (dir.LengthSquared < 1e-9f) return RaycastHit.None;
            var step = _cell * 0.5f;
            for (float t = 0; t <= maxDistance; t += step)
            {
                var p = origin + dir * t;
                WorldToCellClamped(p, out var cx, out var cz);
                if (IsBlockedCell(cx, cz))
                    return new RaycastHit(true, p, t, (dir * -1f).Normalized);
            }
            return RaycastHit.None;
        }

        /// <inheritdoc/>
        public Vec3 SampleNearestWalkable(Vec3 point)
        {
            WorldToCellClamped(point, out var cx, out var cz);
            if (IsWalkableCell(cx, cz)) return CellCenter(cx, cz);
            // Spiral outward for the nearest walkable cell.
            var maxR = Math.Max(_w, _d);
            for (var r = 1; r <= maxR; r++)
            {
                for (var dz = -r; dz <= r; dz++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;   // ring only
                        if (IsWalkableCell(cx + dx, cz + dz)) return CellCenter(cx + dx, cz + dz);
                    }
            }
            return point;
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
        internal byte[] Flags => _flags;
        internal float[] Heights => _height;
    }
}
