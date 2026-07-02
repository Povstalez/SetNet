using System;
using System.Collections.Generic;

namespace SetNet.GeoData
{
    /// <summary>
    /// One <see cref="IGeoData"/> that stitches together many others — the world split into <b>sectors / zones</b>,
    /// each its own baked geodata (grid, layered grid or nav-mesh, in any mix). Queries dispatch to the sector that
    /// contains the point, so the whole sectored world reads as a single seamless surface: walkability, height, sight
    /// and can-walk-straight all work across sector borders. Build one with <see cref="SectoredGeoDataBuilder"/> or
    /// load a baked set via <see cref="GeoDataManifest.Load"/>.
    /// </summary>
    /// <remarks>
    /// A coarse XZ lookup grid indexes the sectors, so a point → sector lookup is ~O(1) regardless of sector count.
    /// Sectors may also stack in Y (a dungeon under a field) — a point resolves to the sector whose Y-range contains it.
    /// </remarks>
    public sealed class SectoredGeoData : IGeoData
    {
        /// <summary>One sector: an id, its geodata, and the world-space box it covers.</summary>
        public readonly struct Sector
        {
            /// <summary>Stable sector id (e.g. "x0_z1"), used by the manifest and for handoff.</summary>
            public readonly string Id;
            /// <summary>The sector's geodata.</summary>
            public readonly IGeoData Geo;
            /// <summary>The world-space bounds the sector owns (usually its geodata's <see cref="IGeoData.Bounds"/>).</summary>
            public readonly Bounds Bounds;

            /// <summary>Creates a sector descriptor.</summary>
            public Sector(string id, IGeoData geo, Bounds bounds) { Id = id; Geo = geo; Bounds = bounds; }
        }

        private readonly Sector[] _sectors;
        private readonly Bounds _bounds;

        // Coarse XZ lookup grid over the union bounds: each grid cell lists the sectors overlapping it.
        private readonly float _ox, _oz;
        private readonly float _cellX, _cellZ;
        private readonly int _gw, _gd;
        private readonly List<int>[] _index;

        /// <summary>The height-step tolerance used when a can-walk-straight check spans a sector border (default 1).</summary>
        public float WalkStepTolerance { get; }

        /// <summary>All sectors.</summary>
        public IReadOnlyList<Sector> Sectors => _sectors;

        internal SectoredGeoData(Sector[] sectors, float walkStepTolerance)
        {
            if (sectors == null) throw new ArgumentNullException(nameof(sectors));
            if (sectors.Length == 0) throw new ArgumentException("At least one sector is required.", nameof(sectors));
            _sectors = sectors;
            WalkStepTolerance = walkStepTolerance;

            // Union bounds.
            var min = sectors[0].Bounds.Min; var max = sectors[0].Bounds.Max;
            foreach (var s in sectors)
            {
                min = new Vec3(Math.Min(min.X, s.Bounds.Min.X), Math.Min(min.Y, s.Bounds.Min.Y), Math.Min(min.Z, s.Bounds.Min.Z));
                max = new Vec3(Math.Max(max.X, s.Bounds.Max.X), Math.Max(max.Y, s.Bounds.Max.Y), Math.Max(max.Z, s.Bounds.Max.Z));
            }
            _bounds = new Bounds(min, max);

            // Build a lookup grid sized ~ 2*sqrt(n) per axis (so ~4n cells) over the union XZ extent.
            var sizeX = Math.Max(1e-3f, max.X - min.X);
            var sizeZ = Math.Max(1e-3f, max.Z - min.Z);
            var per = Math.Max(1, (int)Math.Ceiling(2.0 * Math.Sqrt(sectors.Length)));
            _gw = per; _gd = per;
            _ox = min.X; _oz = min.Z;
            _cellX = sizeX / _gw; _cellZ = sizeZ / _gd;
            _index = new List<int>[_gw * _gd];
            for (var i = 0; i < _index.Length; i++) _index[i] = new List<int>();
            for (var si = 0; si < sectors.Length; si++)
            {
                var b = sectors[si].Bounds;
                GridRange(b.Min.X, b.Max.X, _ox, _cellX, _gw, out var x0, out var x1);
                GridRange(b.Min.Z, b.Max.Z, _oz, _cellZ, _gd, out var z0, out var z1);
                for (var gz = z0; gz <= z1; gz++)
                    for (var gx = x0; gx <= x1; gx++)
                        _index[gz * _gw + gx].Add(si);
            }
        }

        private static void GridRange(float lo, float hi, float origin, float cell, int n, out int i0, out int i1)
        {
            i0 = (int)Math.Floor((lo - origin) / cell);
            i1 = (int)Math.Floor((hi - origin) / cell);
            if (i0 < 0) i0 = 0; if (i0 > n - 1) i0 = n - 1;
            if (i1 < 0) i1 = 0; if (i1 > n - 1) i1 = n - 1;
        }

        /// <inheritdoc/>
        public Bounds Bounds => _bounds;

        /// <summary>Finds the sector that owns a world point (XZ containment, tie-broken by Y). Returns false outside every sector.</summary>
        public bool TryGetSector(Vec3 p, out int sectorIndex)
        {
            sectorIndex = -1;
            var gx = (int)Math.Floor((p.X - _ox) / _cellX);
            var gz = (int)Math.Floor((p.Z - _oz) / _cellZ);
            if (gx < 0 || gx >= _gw || gz < 0 || gz >= _gd) return false;
            var candidates = _index[gz * _gw + gx];
            var bestYGap = float.PositiveInfinity;
            foreach (var si in candidates)
            {
                var b = _sectors[si].Bounds;
                if (p.X < b.Min.X || p.X > b.Max.X || p.Z < b.Min.Z || p.Z > b.Max.Z) continue;   // XZ inside
                if (p.Y >= b.Min.Y && p.Y <= b.Max.Y) { sectorIndex = si; return true; }            // Y inside too → exact
                var gap = p.Y < b.Min.Y ? b.Min.Y - p.Y : p.Y - b.Max.Y;                            // else nearest in Y (stacked)
                if (gap < bestYGap) { bestYGap = gap; sectorIndex = si; }
            }
            return sectorIndex >= 0;
        }

        private IGeoData? SectorAt(Vec3 p) => TryGetSector(p, out var si) ? _sectors[si].Geo : null;

        /// <inheritdoc/>
        public bool IsWalkable(Vec3 point) => SectorAt(point)?.IsWalkable(point) ?? false;

        /// <inheritdoc/>
        public float SampleHeight(Vec3 point) => SectorAt(point)?.SampleHeight(point) ?? float.NaN;

        /// <inheritdoc/>
        public Vec3 SampleNearestWalkable(Vec3 point)
        {
            var direct = SectorAt(point);
            if (direct != null) return direct.SampleNearestWalkable(point);
            // Outside every sector: snap within the nearest sector by bounds distance.
            var best = -1; var bestD = float.PositiveInfinity;
            for (var i = 0; i < _sectors.Length; i++)
            {
                var d = DistanceToBoundsXZ(_sectors[i].Bounds, point);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best >= 0 ? _sectors[best].Geo.SampleNearestWalkable(point) : point;
        }

        /// <inheritdoc/>
        public bool LineOfSight(Vec3 from, Vec3 to)
        {
            // Same sector → delegate exactly. Across a border → split at each sector boundary and delegate each piece.
            if (TryGetSector(from, out var a) && TryGetSector(to, out var b) && a == b)
                return _sectors[a].Geo.LineOfSight(from, to);
            return SegmentBySector(from, to, (geo, p0, p1) => geo.LineOfSight(p0, p1), heightAware: false);
        }

        /// <inheritdoc/>
        public bool CanWalkStraight(Vec3 from, Vec3 to)
        {
            if (TryGetSector(from, out var a) && TryGetSector(to, out var b) && a == b)
                return _sectors[a].Geo.CanWalkStraight(from, to);
            return SegmentBySector(from, to, (geo, p0, p1) => geo.CanWalkStraight(p0, p1), heightAware: true);
        }

        /// <inheritdoc/>
        public RaycastHit Raycast(Vec3 origin, Vec3 direction, float maxDistance)
        {
            // March sector by sector: delegate to the sector under the ray, then hop to where the ray leaves that
            // sector's XZ box and continue, until a hit or the distance runs out.
            var dir = direction.Normalized;
            if (dir.LengthSquared < 1e-9f) return RaycastHit.None;
            var travelled = 0f;
            var p = origin;
            for (var guard = 0; guard < _sectors.Length + 2 && travelled <= maxDistance; guard++)
            {
                if (!TryGetSector(p + dir * 1e-3f, out var si))
                {
                    // Not in a sector: advance to the union bounds or bail.
                    var step = _cellX + _cellZ;
                    p += dir * step; travelled += step;
                    continue;
                }
                var remaining = maxDistance - travelled;
                var hit = _sectors[si].Geo.Raycast(p, dir, remaining);
                if (hit.Hit) return hit;
                var exit = ExitDistanceXZ(_sectors[si].Bounds, p, dir);
                if (exit <= 0f || float.IsInfinity(exit)) return RaycastHit.None;
                p += dir * (exit + 1e-3f);
                travelled += exit + 1e-3f;
            }
            return RaycastHit.None;
        }

        // Splits (from,to) at each sector boundary it crosses and delegates every piece — fully contained in one
        // sector — to that sector's own exact check. For height-aware checks, the ground-height jump across a border
        // must also stay within WalkStepTolerance. Returns false if any piece falls outside every sector.
        private bool SegmentBySector(Vec3 from, Vec3 to, Func<IGeoData, Vec3, Vec3, bool> check, bool heightAware)
        {
            const float eps = 0.05f;   // keep piece endpoints just inside a sector (grid edges are exclusive)
            var seg = to - from;
            var segLen = seg.Length;
            if (segLen < 1e-5f) return SectorAt(from)?.IsWalkable(from) ?? false;
            var dir = seg * (1f / segLen);

            var travelled = 0f;
            var prevH = float.NaN;
            for (var guard = 0; guard < _sectors.Length + 2; guard++)
            {
                var p = from + dir * travelled;
                if (!TryGetSector(p + dir * eps, out var si)) return false;          // gap between sectors
                var geo = _sectors[si].Geo;

                var remaining = segLen - travelled;
                var exit = ExitDistanceXZ(_sectors[si].Bounds, p, dir);
                if (exit <= 0f) exit = remaining;
                var atBorder = exit < remaining - 1e-4f;
                var spanLen = Math.Min(exit, remaining);
                var pEnd = from + dir * (travelled + (atBorder ? Math.Max(0f, spanLen - eps) : spanLen));

                if (heightAware)
                {
                    var entryH = geo.SampleHeight(p + dir * eps);
                    if (!float.IsNaN(prevH) && !float.IsNaN(entryH) && Math.Abs(entryH - prevH) > WalkStepTolerance) return false;
                }
                if (!check(geo, p, pEnd)) return false;
                if (heightAware) prevH = geo.SampleHeight(pEnd);

                travelled += atBorder ? spanLen + eps : spanLen;
                if (travelled >= segLen - 1e-4f) return true;
            }
            return true;
        }

        private static float DistanceToBoundsXZ(Bounds b, Vec3 p)
        {
            var dx = p.X < b.Min.X ? b.Min.X - p.X : (p.X > b.Max.X ? p.X - b.Max.X : 0f);
            var dz = p.Z < b.Min.Z ? b.Min.Z - p.Z : (p.Z > b.Max.Z ? p.Z - b.Max.Z : 0f);
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // Distance along (origin,dir) to leave the box on the XZ plane (slab test on X and Z only).
        private static float ExitDistanceXZ(Bounds b, Vec3 origin, Vec3 dir)
        {
            var tExit = float.PositiveInfinity;
            tExit = SlabExit(origin.X, dir.X, b.Min.X, b.Max.X, tExit);
            tExit = SlabExit(origin.Z, dir.Z, b.Min.Z, b.Max.Z, tExit);
            return tExit;
        }

        private static float SlabExit(float o, float d, float lo, float hi, float tExit)
        {
            if (Math.Abs(d) < 1e-9f) return tExit;   // parallel: never exits on this axis
            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            var tFar = Math.Max(t1, t2);
            if (tFar > 0 && tFar < tExit) tExit = tFar;
            return tExit;
        }
    }
}
