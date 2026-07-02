using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// A* across a <see cref="SectoredGeoData"/> (a world split into sector/zone geodatas). Within a single sector it
    /// delegates to that sector's native pathfinder (grid / layered / nav-mesh — exact, and each is built once and
    /// reused). Across sectors it routes over the sector-adjacency graph, then stitches a path through the border
    /// between each pair of sectors ("portal" points), so an entity can path seamlessly from one zone into another.
    /// </summary>
    /// <remarks>Cross-sector stitching assumes sectors tile the XZ plane edge-to-edge (the usual case). Movement
    /// within a sector is always exact; the border portal is the straight-line crossing snapped to walkable ground.</remarks>
    public sealed class SectoredPathfinder : IPathfinder
    {
        private readonly SectoredGeoData _world;
        private readonly IPathfinder[] _finders;                 // one per sector, built lazily and reused
        private List<int>[]? _adjacency;                         // sector graph, built lazily

        /// <summary>Creates a pathfinder over the sectored world.</summary>
        public SectoredPathfinder(SectoredGeoData world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _finders = new IPathfinder[world.Sectors.Count];
        }

        private IPathfinder FinderFor(int sector) => _finders[sector] ??= Pathfinding.For(_world.Sectors[sector].Geo);

        /// <inheritdoc/>
        public Path FindPath(Vec3 from, Vec3 to)
        {
            if (!ResolveSector(ref from, out var a) || !ResolveSector(ref to, out var b)) return Path.Empty;
            if (a == b) return FinderFor(a).FindPath(from, to);

            var chain = SectorRoute(a, b);
            if (chain == null) return Path.Empty;

            var pts = new List<Vec3>();
            var current = from;
            for (var i = 0; i < chain.Count - 1; i++)
            {
                if (!TryPortal(_world.Sectors[chain[i]].Bounds, _world.Sectors[chain[i + 1]].Bounds, current, to, out var portal))
                    return Path.Empty;
                var leg = FinderFor(chain[i]).FindPath(current, portal);
                if (leg.IsEmpty) return Path.Empty;
                Append(pts, leg);
                current = portal;
            }
            var last = FinderFor(chain[chain.Count - 1]).FindPath(current, to);
            if (last.IsEmpty) return Path.Empty;
            Append(pts, last);

            return new Path(pts);
        }

        // Finds the sector for a point; if the point is outside every sector, snaps it onto the nearest walkable ground.
        private bool ResolveSector(ref Vec3 p, out int sector)
        {
            if (_world.TryGetSector(p, out sector)) return true;
            p = _world.SampleNearestWalkable(p);
            return _world.TryGetSector(p, out sector);
        }

        private static void Append(List<Vec3> pts, Path leg)
        {
            foreach (var w in leg.Waypoints)
            {
                if (pts.Count > 0 && Vec3.Distance(pts[pts.Count - 1], w) < 1e-4f) continue;   // dedupe the seam
                pts.Add(w);
            }
        }

        // --- sector adjacency graph ---

        private void EnsureAdjacency()
        {
            if (_adjacency != null) return;
            var n = _world.Sectors.Count;
            var adj = new List<int>[n];
            for (var i = 0; i < n; i++) adj[i] = new List<int>();
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    if (AreNeighbours(_world.Sectors[i].Bounds, _world.Sectors[j].Bounds))
                    {
                        adj[i].Add(j); adj[j].Add(i);
                    }
            _adjacency = adj;
        }

        // Two sectors neighbour if their XZ footprints, grown by a small margin, overlap (edge- or corner-adjacent).
        private static bool AreNeighbours(Bounds a, Bounds b)
        {
            var eps = 0.05f * Math.Min(
                Math.Min(a.Size.X, a.Size.Z),
                Math.Min(b.Size.X, b.Size.Z));
            if (eps < 0.01f) eps = 0.01f;
            return a.Min.X - eps <= b.Max.X && b.Min.X - eps <= a.Max.X &&
                   a.Min.Z - eps <= b.Max.Z && b.Min.Z - eps <= a.Max.Z;
        }

        // Dijkstra over the (small) sector graph, weighted by centre-to-centre distance.
        private List<int>? SectorRoute(int start, int goal)
        {
            EnsureAdjacency();
            var adj = _adjacency!;
            var n = _world.Sectors.Count;
            var dist = new float[n];
            var prev = new int[n];
            var done = new bool[n];
            for (var i = 0; i < n; i++) { dist[i] = float.PositiveInfinity; prev[i] = -1; }
            dist[start] = 0f;

            for (var iter = 0; iter < n; iter++)
            {
                var u = -1; var best = float.PositiveInfinity;
                for (var i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u < 0) break;
                if (u == goal) break;
                done[u] = true;
                foreach (var v in adj[u])
                {
                    var w = Vec3.Distance(_world.Sectors[u].Bounds.Center, _world.Sectors[v].Bounds.Center);
                    if (dist[u] + w < dist[v]) { dist[v] = dist[u] + w; prev[v] = u; }
                }
            }
            if (float.IsInfinity(dist[goal])) return null;

            var chain = new List<int>();
            for (var at = goal; at != -1; at = prev[at]) { chain.Add(at); if (at == start) break; }
            chain.Reverse();
            return chain.Count > 0 && chain[0] == start ? chain : null;
        }

        // The crossing point on the shared border of two adjacent boxes, along the current->goal line, snapped walkable.
        private bool TryPortal(Bounds bi, Bounds bj, Vec3 from, Vec3 to, out Vec3 portal)
        {
            portal = default;
            // Overlap ranges on each axis.
            float oxLo = Math.Max(bi.Min.X, bj.Min.X), oxHi = Math.Min(bi.Max.X, bj.Max.X);
            float ozLo = Math.Max(bi.Min.Z, bj.Min.Z), ozHi = Math.Min(bi.Max.Z, bj.Max.Z);

            // Decide the separating axis: sectors touch along X (a vertical border) or along Z (a horizontal border).
            var gapX = Math.Max(bj.Min.X - bi.Max.X, bi.Min.X - bj.Max.X);   // >~0 means separated on X
            var gapZ = Math.Max(bj.Min.Z - bi.Max.Z, bi.Min.Z - bj.Max.Z);

            float px, pz;
            if (gapX >= gapZ)
            {
                // Vertical border at the shared X; pick Z from the crossing, clamped to the shared span.
                px = 0.5f * (Math.Max(bi.Min.X, bj.Min.X) + Math.Min(bi.Max.X, bj.Max.X));
                if (ozHi < ozLo) return false;
                pz = CrossCoord(from.X, from.Z, to.X, to.Z, px);
                pz = Clamp(pz, ozLo, ozHi);
            }
            else
            {
                // Horizontal border at the shared Z; pick X from the crossing.
                pz = 0.5f * (Math.Max(bi.Min.Z, bj.Min.Z) + Math.Min(bi.Max.Z, bj.Max.Z));
                if (oxHi < oxLo) return false;
                px = CrossCoord(from.Z, from.X, to.Z, to.X, pz);
                px = Clamp(px, oxLo, oxHi);
            }

            var y = 0.5f * (from.Y + to.Y);
            portal = _world.SampleNearestWalkable(new Vec3(px, y, pz));   // snap onto real ground near the border
            return true;
        }

        // Given a segment (a0,b0)->(a1,b1) parameterised by the first coordinate, the second coordinate where a == target.
        private static float CrossCoord(float a0, float b0, float a1, float b1, float aTarget)
        {
            var da = a1 - a0;
            if (Math.Abs(da) < 1e-6f) return 0.5f * (b0 + b1);
            var t = (aTarget - a0) / da;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return b0 + (b1 - b0) * t;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
