using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>Greedy string-pulling: drop intermediate waypoints wherever the geometry allows walking straight between two farther-apart ones.</summary>
    internal static class PathSmoothing
    {
        public static List<Vec3> StringPull(IGeoData geo, IReadOnlyList<Vec3> pts)
        {
            if (pts.Count <= 2) return new List<Vec3>(pts);
            var result = new List<Vec3> { pts[0] };
            var i = 0;
            while (i < pts.Count - 1)
            {
                var j = pts.Count - 1;
                while (j > i + 1 && !geo.CanWalkStraight(pts[i], pts[j])) j--;
                result.Add(pts[j]);
                i = j;
            }
            return result;
        }
    }
}
