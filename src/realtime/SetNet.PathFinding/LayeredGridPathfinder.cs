using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// A* over a <see cref="LayeredGridGeoData"/> (multi-storey grid). Nodes are <i>layers</i>, not cells: from a layer
    /// the search steps to the neighbour cell's layer nearest in height and within one <see cref="LayeredGridGeoData.MaxStep"/>,
    /// so a route naturally climbs stairs and crosses bridges instead of teleporting between floors. 8-connected, no
    /// diagonal corner-cutting; working memory is pooled and generation-stamped like <see cref="GridPathfinder"/> so
    /// queries are allocation-free on the hot path.
    /// </summary>
    public sealed class LayeredGridPathfinder : IPathfinder
    {
        private readonly LayeredGridGeoData _g;
        private readonly SearchStatePool _pool = new SearchStatePool();

        /// <summary>Maximum nodes A* may expand before returning <see cref="Path.Empty"/>. Default unbounded; lower under load. See <see cref="GridPathfinder.MaxExpansions"/>.</summary>
        public int MaxExpansions { get; set; } = int.MaxValue;

        /// <summary>Creates a pathfinder over the given layered grid.</summary>
        public LayeredGridPathfinder(LayeredGridGeoData grid) => _g = grid ?? throw new ArgumentNullException(nameof(grid));

        /// <inheritdoc/>
        public Path FindPath(Vec3 from, Vec3 to)
        {
            if (!_g.TrySnapLayer(from, out var startId) || !_g.TrySnapLayer(to, out var goalId)) return Path.Empty;
            if (startId == goalId) return new Path(new[] { from, to });

            var w = _g.Width;
            var cell = _g.CellSize;

            var s = _pool.Rent();
            try
            {
                s.Begin(_g.LayerCount);
                s.Relax(startId, 0f, startId);
                s.Open.Push(startId, Heuristic(startId, goalId, w, cell));

                var expansions = 0;
                while (s.Open.Count > 0)
                {
                    var cur = s.Open.Pop();
                    if (s.IsClosed(cur)) continue;
                    if (cur == goalId) return Build(s, startId, goalId, from, to);
                    s.Close(cur);
                    if (++expansions > MaxExpansions) return Path.Empty;

                    var gCur = s.GScore(cur);
                    var ci = _g.LayerCellIndex(cur);
                    int cx = ci % w, cz = ci / w;
                    var h = _g.LayerHeight(cur);
                    var centre = _g.LayerCenter(cur);

                    for (var dz = -1; dz <= 1; dz++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            if (!_g.TryLayerNear(cx + dx, cz + dz, h, _g.MaxStep, out var nid)) continue;
                            // No diagonal corner-cutting: both orthogonal neighbours must have a reachable layer too.
                            if (dx != 0 && dz != 0 &&
                                (!_g.TryLayerNear(cx + dx, cz, h, _g.MaxStep, out _) ||
                                 !_g.TryLayerNear(cx, cz + dz, h, _g.MaxStep, out _))) continue;
                            if (s.IsClosed(nid)) continue;
                            var step = Vec3.Distance(centre, _g.LayerCenter(nid));   // true 3D cost (accounts for the climb)
                            var tentative = gCur + step;
                            if (tentative < s.GScore(nid))
                            {
                                s.Relax(nid, tentative, cur);
                                s.Open.Push(nid, tentative + Heuristic(nid, goalId, w, cell));
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

        private Path Build(SearchState s, int startId, int goalId, Vec3 from, Vec3 to)
        {
            var layers = new List<int>();
            var id = goalId;
            while (true) { layers.Add(id); if (id == startId) break; id = s.CameFrom(id); }
            layers.Reverse();

            var pts = new List<Vec3>(layers.Count);
            foreach (var l in layers) pts.Add(_g.LayerCenter(l));
            pts[0] = from;
            pts[pts.Count - 1] = to;

            return new Path(PathSmoothing.StringPull(_g, pts));
        }

        private float Heuristic(int a, int b, int w, float cell)
        {
            int ca = _g.LayerCellIndex(a), cb = _g.LayerCellIndex(b);
            int ax = ca % w, az = ca / w, bx = cb % w, bz = cb / w;
            int dx = Math.Abs(ax - bx), dz = Math.Abs(az - bz);
            // Octile XZ distance (admissible — it never over-estimates; the vertical climb only adds to the true cost).
            return cell * ((dx + dz) + (1.41421356f - 2f) * Math.Min(dx, dz));
        }
    }
}
