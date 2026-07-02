using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// A* over a <see cref="NavMeshGeoData"/>'s triangle adjacency, then a portal-midpoint path straightened by
    /// line-of-walk smoothing (a robust alternative to a full funnel; the smoothing pulls the path taut wherever the
    /// mesh lets you walk straight).
    /// </summary>
    public sealed class NavMeshPathfinder : IPathfinder
    {
        private readonly NavMeshGeoData _m;

        /// <summary>Creates a pathfinder over the given nav-mesh.</summary>
        public NavMeshPathfinder(NavMeshGeoData mesh) => _m = mesh ?? throw new ArgumentNullException(nameof(mesh));

        /// <inheritdoc/>
        public Path FindPath(Vec3 from, Vec3 to)
        {
            var f = from; var t = to;
            var startTri = _m.TriangleAt(f);
            if (startTri < 0) { f = _m.SampleNearestWalkable(f); startTri = _m.TriangleAt(f); }
            var goalTri = _m.TriangleAt(t);
            if (goalTri < 0) { t = _m.SampleNearestWalkable(t); goalTri = _m.TriangleAt(t); }
            if (startTri < 0 || goalTri < 0) return Path.Empty;
            if (startTri == goalTri) return new Path(new[] { from, to });

            int n = _m.TriangleCount;
            var g = new float[n];
            var came = new int[n];
            var closed = new bool[n];
            for (var i = 0; i < n; i++) { g[i] = float.PositiveInfinity; came[i] = -1; }

            var goalC = _m.TriangleCentroid(goalTri);
            g[startTri] = 0;
            var open = new MinHeap();
            open.Push(startTri, Vec3.Distance(_m.TriangleCentroid(startTri), goalC));

            var found = false;
            while (open.Count > 0)
            {
                var cur = open.Pop();
                if (closed[cur]) continue;
                if (cur == goalTri) { found = true; break; }
                closed[cur] = true;
                var curC = _m.TriangleCentroid(cur);
                for (var e = 0; e < 3; e++)
                {
                    var nb = _m.Neighbour(cur, e);
                    if (nb < 0 || closed[nb]) continue;
                    var nbC = _m.TriangleCentroid(nb);
                    var tentative = g[cur] + Vec3.Distance(curC, nbC);
                    if (tentative < g[nb])
                    {
                        g[nb] = tentative;
                        came[nb] = cur;
                        open.Push(nb, tentative + Vec3.Distance(nbC, goalC));
                    }
                }
            }
            if (!found) return Path.Empty;

            var tris = new List<int>();
            var c = goalTri;
            while (c != -1) { tris.Add(c); if (c == startTri) break; c = came[c]; }
            tris.Reverse();

            var pts = new List<Vec3> { from };
            for (var i = 0; i < tris.Count - 1; i++)
                if (_m.SharedEdge(tris[i], tris[i + 1], out var v0, out var v1))
                    pts.Add((v0 + v1) * 0.5f);
            pts.Add(to);

            return new Path(PathSmoothing.StringPull(_m, pts));
        }
    }
}
