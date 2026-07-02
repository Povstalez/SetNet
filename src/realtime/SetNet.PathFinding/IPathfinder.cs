using SetNet.GeoData;

namespace SetNet.PathFinding
{
    /// <summary>
    /// Finds a walkable path between two world points over some <see cref="IGeoData"/>. Get one for your world via
    /// <see cref="Pathfinding.For"/>; the concrete implementation (grid A* vs nav-mesh A*+funnel) is chosen from the
    /// geometry kind.
    /// </summary>
    public interface IPathfinder
    {
        /// <summary>Finds a path from <paramref name="from"/> to <paramref name="to"/>, or <see cref="Path.Empty"/> if none exists.</summary>
        Path FindPath(Vec3 from, Vec3 to);
    }
}
