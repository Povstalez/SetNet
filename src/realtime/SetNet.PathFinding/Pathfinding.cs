using System;
using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>Entry point: get the right <see cref="IPathfinder"/> for a piece of <see cref="IGeoData"/>.</summary>
    public static class Pathfinding
    {
        /// <summary>Returns a pathfinder matching the geometry kind (grid / multi-storey grid A*, or nav-mesh A*+funnel).</summary>
        public static IPathfinder For(IGeoData geo)
        {
            // Власний пошуковик має пріоритет над усім вбудованим.
            if (geo is IPathfinderProvider provider) return provider.CreatePathfinder();

            switch (geo)
            {
                case GridGeoData g: return new GridPathfinder(g);
                case LayeredGridGeoData l: return new LayeredGridPathfinder(l);
                case NavMeshGeoData m: return new NavMeshPathfinder(m);
                case SectoredGeoData s: return new SectoredPathfinder(s);
                default: throw new NotSupportedException($"No pathfinder for {geo?.GetType().Name ?? "null"}.");
            }
        }
    }
}
