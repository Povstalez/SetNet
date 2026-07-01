using System;
using System.Collections.Generic;
using SetNet.Core;

namespace SetNet.StateSync.SpatialGrid
{
    /// <summary>
    /// An <see cref="IInterestManager"/> that buckets entities into a uniform 3D grid so each observer only tests the
    /// entities in nearby cells instead of the whole world — O(neighbours) per observer instead of O(N). Ideal for large
    /// worlds with many entities. The grid is rebuilt once per tick (cached by the entity-list instance the server passes
    /// to every observer that tick) and reused across observers. You supply how to read an entity's and an observer's
    /// position, since the core doesn't know which field is "position".
    /// </summary>
    public sealed class SpatialGridInterest : IInterestManager
    {
        private readonly Func<NetworkEntity, Vec3> _entityPosition;
        private readonly Func<BasePeer, Vec3> _observerPosition;
        private readonly float _cellSize;
        private readonly float _radius;
        private readonly bool _alwaysSeeOwned;

        private readonly object _gate = new object();
        private IReadOnlyCollection<NetworkEntity>? _builtFrom;
        private Dictionary<long, List<NetworkEntity>> _grid = new Dictionary<long, List<NetworkEntity>>();

        /// <summary>Creates a spatial-grid interest manager.</summary>
        /// <param name="entityPosition">Reads an entity's world position.</param>
        /// <param name="observerPosition">Reads an observer's focus position.</param>
        /// <param name="radius">Visibility radius (world units).</param>
        /// <param name="cellSize">Grid cell size; a good default is roughly the radius. If ≤ 0, uses <paramref name="radius"/>.</param>
        /// <param name="alwaysSeeOwnedEntities">When true, an observer always sees entities it owns, even outside the radius.</param>
        public SpatialGridInterest(Func<NetworkEntity, Vec3> entityPosition, Func<BasePeer, Vec3> observerPosition,
            float radius, float cellSize = 0, bool alwaysSeeOwnedEntities = true)
        {
            _entityPosition = entityPosition ?? throw new ArgumentNullException(nameof(entityPosition));
            _observerPosition = observerPosition ?? throw new ArgumentNullException(nameof(observerPosition));
            _radius = radius;
            _cellSize = cellSize > 0 ? cellSize : Math.Max(1f, radius);
            _alwaysSeeOwned = alwaysSeeOwnedEntities;
        }

        /// <inheritdoc/>
        public IEnumerable<NetworkEntity> Query(BasePeer observer, IReadOnlyCollection<NetworkEntity> all)
        {
            EnsureGrid(all);

            var focus = _observerPosition(observer);
            var ownerId = observer.CurrentPeerInfo.Id;
            var r2 = _radius * _radius;
            var reach = (int)Math.Ceiling(_radius / _cellSize);
            int fx = CellCoord(focus.X), fy = CellCoord(focus.Y), fz = CellCoord(focus.Z);

            var seen = new HashSet<uint>();
            for (var dx = -reach; dx <= reach; dx++)
            for (var dy = -reach; dy <= reach; dy++)
            for (var dz = -reach; dz <= reach; dz++)
            {
                if (!_grid.TryGetValue(Key(fx + dx, fy + dy, fz + dz), out var bucket)) continue;
                foreach (var e in bucket)
                {
                    if (!seen.Add(e.NetId)) continue;
                    if (Vec3.DistanceSquared(_entityPosition(e), focus) <= r2) yield return e;
                    else seen.Remove(e.NetId);   // in a neighbouring cell but outside the radius — may still be owned below
                }
            }

            if (_alwaysSeeOwned)
                foreach (var e in all)
                    if (e.Owner == ownerId && ownerId != Guid.Empty && seen.Add(e.NetId))
                        yield return e;
        }

        private void EnsureGrid(IReadOnlyCollection<NetworkEntity> all)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_builtFrom, all)) return;   // same tick's list → reuse
                var grid = new Dictionary<long, List<NetworkEntity>>();
                foreach (var e in all)
                {
                    var p = _entityPosition(e);
                    var key = Key(CellCoord(p.X), CellCoord(p.Y), CellCoord(p.Z));
                    if (!grid.TryGetValue(key, out var bucket)) { bucket = new List<NetworkEntity>(); grid[key] = bucket; }
                    bucket.Add(e);
                }
                _grid = grid;
                _builtFrom = all;
            }
        }

        private int CellCoord(float v) => (int)Math.Floor(v / _cellSize);

        // Pack 3 cell coords into one long key (21 bits each, biased). Fine for typical world extents.
        private static long Key(int x, int y, int z)
            => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }
}
