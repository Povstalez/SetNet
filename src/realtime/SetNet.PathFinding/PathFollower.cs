using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// Walks an entity along a <see cref="Path"/>: each step, move the current position toward the next waypoint by
    /// up to a budget (speed × dt), advancing waypoints as they're reached. This is what <c>SetNet.Mobs</c> movement
    /// uses to turn a <c>MoveTo</c> intent into authoritative motion.
    /// </summary>
    public sealed class PathFollower
    {
        private readonly Path _path;
        private int _index;

        /// <summary>Starts following the given path from its first waypoint.</summary>
        public PathFollower(Path path) { _path = path; _index = 0; }

        /// <summary>True once the last waypoint has been reached (or the path was empty).</summary>
        public bool Arrived => _path.IsEmpty || _index >= _path.Waypoints.Count;

        /// <summary>The current target waypoint (or <see cref="Vec3.Zero"/> when arrived).</summary>
        public Vec3 Target => Arrived ? Vec3.Zero : _path.Waypoints[_index];

        /// <summary>
        /// The route being followed, exactly as the pathfinder produced it.
        ///
        /// <para>
        /// Exposed because the route is worth more than the destination alone. A server that replicates only
        /// "walking to X" makes every client re-run the same search — once per moving entity, on the frame the
        /// order arrives. Handing out the polyline lets the server publish what it has already computed instead
        /// of paying for it a second time.
        /// </para>
        /// </summary>
        public Path Route => _path;

        /// <summary>
        /// Index of the waypoint currently being walked toward — everything before it is already behind.
        /// Together with <see cref="Route"/> this describes the remaining route without allocating a second list.
        /// </summary>
        public int Index => _index;

        /// <summary>
        /// Advances from <paramref name="current"/> toward the path by at most <paramref name="maxDistance"/> world
        /// units and returns the new position, consuming waypoints reached along the way.
        /// </summary>
        public Vec3 Step(Vec3 current, float maxDistance)
        {
            var pos = current;
            var remaining = maxDistance;
            while (!Arrived && remaining > 1e-4f)
            {
                var wp = _path.Waypoints[_index];
                var to = wp - pos;
                var dist = to.Length;
                if (dist <= remaining) { pos = wp; _index++; remaining -= dist; }
                else { pos = pos + to.Normalized * remaining; remaining = 0f; }
            }
            return pos;
        }
    }
}
