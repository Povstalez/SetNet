using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// A* over a <see cref="GridGeoData"/>: 8-connected, octile heuristic, no diagonal corner-cutting, then
    /// straight-line smoothing. Built once and reused for every agent — working memory is pooled and generation-stamped
    /// so a query allocates nothing on the hot path (see <see cref="SearchState"/>), making it cheap to path thousands
    /// of characters/mobs per second over a large map.
    /// </summary>
    public sealed class GridPathfinder : IPathfinder
    {
        private readonly GridGeoData _g;
        private readonly SearchStatePool _pool = new SearchStatePool();

        /// <summary>
        /// Maximum number of nodes A* may expand before giving up and returning <see cref="Path.Empty"/>. Bounds the
        /// worst case (an unreachable goal on a huge open map would otherwise scan the whole reachable area). Default
        /// <see cref="int.MaxValue"/> (unbounded — exact results); lower it to cap per-query cost under heavy load.
        /// </summary>
        public int MaxExpansions { get; set; } = int.MaxValue;

        /// <summary>Creates a pathfinder over the given grid.</summary>
        public GridPathfinder(GridGeoData grid) => _g = grid ?? throw new ArgumentNullException(nameof(grid));

        /// <inheritdoc/>
        public Path FindPath(Vec3 from, Vec3 to)
        {
            var start = SnapCell(from);
            var goal = SnapCell(to);
            if (start.x < 0 || goal.x < 0) return Path.Empty;
            if (start.x == goal.x && start.z == goal.z) return new Path(new[] { from, to });

            int w = _g.Width, d = _g.Depth, n = w * d;
            int startId = start.z * w + start.x, goalId = goal.z * w + goal.x;

            var cell = _g.CellSize;
            var diag = cell * 1.41421356f;

            var s = _pool.Rent();
            try
            {
                s.Begin(n);
                s.Relax(startId, 0f, startId);
                s.Open.Push(startId, Heuristic(start.x, start.z, goal.x, goal.z));

                var expansions = 0;
                while (s.Open.Count > 0)
                {
                    var cur = s.Open.Pop();
                    if (s.IsClosed(cur)) continue;               // stale heap entry (a cheaper path was found later)
                    if (cur == goalId) return Build(s, startId, goalId, w, from, to);
                    s.Close(cur);
                    if (++expansions > MaxExpansions) return Path.Empty;

                    int cx = cur % w, cz = cur / w;
                    var gCur = s.GScore(cur);

                    for (var dz = -1; dz <= 1; dz++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int nx = cx + dx, nz = cz + dz;
                            if (!_g.IsWalkableCell(nx, nz)) continue;
                            // No diagonal corner-cutting: both orthogonal neighbours must be walkable.
                            if (dx != 0 && dz != 0 && (!_g.IsWalkableCell(cx + dx, cz) || !_g.IsWalkableCell(cx, cz + dz))) continue;
                            int nid = nz * w + nx;
                            if (s.IsClosed(nid)) continue;
                            var step = (dx != 0 && dz != 0) ? diag : cell;
                            var tentative = gCur + step;
                            if (tentative < s.GScore(nid))
                            {
                                s.Relax(nid, tentative, cur);
                                s.Open.Push(nid, tentative + Heuristic(nx, nz, goal.x, goal.z));
                            }
                        }
                }
                return Path.Empty;
            }
            finally
            {
                _pool.Return(s);
            }
        }

        private Path Build(SearchState s, int startId, int goalId, int w, Vec3 from, Vec3 to)
        {
            var cells = new List<int>();
            var id = goalId;
            while (true) { cells.Add(id); if (id == startId) break; id = s.CameFrom(id); }
            cells.Reverse();

            var pts = new List<Vec3>(cells.Count);
            foreach (var cid in cells) pts.Add(_g.CellCenter(cid % w, cid / w));
            pts[0] = from;
            pts[pts.Count - 1] = to;

            return new Path(PathSmoothing.StringPull(_g, pts));
        }

        private (int x, int z) SnapCell(Vec3 p)
        {
            if (_g.WorldToCell(p, out var cx, out var cz) && _g.IsWalkableCell(cx, cz)) return (cx, cz);
            var snapped = _g.SampleNearestWalkable(p);
            return _g.WorldToCell(snapped, out cx, out cz) && _g.IsWalkableCell(cx, cz) ? (cx, cz) : (-1, -1);
        }

        private float Heuristic(int ax, int az, int bx, int bz)
        {
            int dx = Math.Abs(ax - bx), dz = Math.Abs(az - bz);
            // Octile distance scaled by cell size.
            return _g.CellSize * ((dx + dz) + (1.41421356f - 2f) * Math.Min(dx, dz));
        }
    }
}
