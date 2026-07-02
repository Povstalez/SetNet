using System;

namespace SetNet.GeoData
{
    /// <summary>
    /// Builds a <see cref="GridGeoData"/>. Start with the grid dimensions, mark cells walkable (with a ground height)
    /// or blocked (a wall), then <see cref="Build"/>. This is what a baker (e.g. the Unity tool) drives — one call
    /// per cell — or you can fill it programmatically for tests.
    /// </summary>
    public sealed class GridGeoDataBuilder
    {
        private readonly Vec3 _origin;
        private readonly float _cell;
        private readonly int _w, _d;
        private readonly byte[] _flags;
        private readonly float[] _height;
        private float _maxStep = 1f;

        /// <summary>Creates a builder for a <paramref name="width"/>×<paramref name="depth"/> grid of <paramref name="cellSize"/>-sized cells whose (0,0) corner is at <paramref name="origin"/>. All cells start empty (not walkable, not blocked).</summary>
        public GridGeoDataBuilder(Vec3 origin, float cellSize, int width, int depth)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
            _origin = origin; _cell = cellSize; _w = width; _d = depth;
            _flags = new byte[width * depth];
            _height = new float[width * depth];
        }

        /// <summary>Marks a cell walkable at the given ground height (clears any blocked flag).</summary>
        public GridGeoDataBuilder SetWalkable(int cx, int cz, float height = 0f)
        {
            if (InBounds(cx, cz)) { var i = cz * _w + cx; _flags[i] = GridGeoData.FlagWalkable; _height[i] = height; }
            return this;
        }

        /// <summary>Marks a cell blocked — a wall/obstacle that stops sight and movement (clears walkable).</summary>
        public GridGeoDataBuilder SetBlocked(int cx, int cz)
        {
            if (InBounds(cx, cz)) _flags[cz * _w + cx] = GridGeoData.FlagBlocked;
            return this;
        }

        /// <summary>Sets the maximum climbable ground-height step between adjacent cells (default 1).</summary>
        public GridGeoDataBuilder SetMaxStep(float maxStep) { _maxStep = maxStep; return this; }

        /// <summary>Fills every cell from a callback returning (walkable, blocked, height) for each (cx, cz).</summary>
        public GridGeoDataBuilder Fill(Func<int, int, (bool walkable, bool blocked, float height)> cell)
        {
            for (var cz = 0; cz < _d; cz++)
                for (var cx = 0; cx < _w; cx++)
                {
                    var (w, b, h) = cell(cx, cz);
                    var i = cz * _w + cx;
                    _flags[i] = b ? GridGeoData.FlagBlocked : (w ? GridGeoData.FlagWalkable : (byte)0);
                    _height[i] = h;
                }
            return this;
        }

        /// <summary>Builds the immutable grid.</summary>
        public GridGeoData Build() => new GridGeoData(_origin, _cell, _w, _d, _flags, _height, _maxStep);

        private bool InBounds(int cx, int cz) => cx >= 0 && cx < _w && cz >= 0 && cz < _d;
    }
}
