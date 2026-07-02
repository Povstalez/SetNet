using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>A computed path: an ordered list of world-space waypoints from the start to the goal.</summary>
    public sealed class Path
    {
        /// <summary>The waypoints, start first, goal last.</summary>
        public IReadOnlyList<Vec3> Waypoints { get; }

        /// <summary>Total path length (sum of segment lengths).</summary>
        public float Length { get; }

        /// <summary>True when the path has no waypoints (no route found).</summary>
        public bool IsEmpty => Waypoints.Count == 0;

        /// <summary>Creates a path from its waypoints.</summary>
        public Path(IReadOnlyList<Vec3> waypoints)
        {
            Waypoints = waypoints;
            float len = 0;
            for (var i = 1; i < waypoints.Count; i++) len += Vec3.Distance(waypoints[i - 1], waypoints[i]);
            Length = len;
        }

        /// <summary>An empty path (no route).</summary>
        public static readonly Path Empty = new Path(new Vec3[0]);
    }
}
