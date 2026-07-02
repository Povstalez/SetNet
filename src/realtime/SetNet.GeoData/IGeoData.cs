namespace SetNet.GeoData
{
    /// <summary>
    /// Server-side knowledge of the game world/scene: where you can stand, what blocks sight or movement, and where
    /// the ground is. Two implementations back it — a <see cref="GridGeoData"/> (nav-grid, cheap, auto-bakeable) and a
    /// <see cref="NavMeshGeoData"/> (nav-mesh, precise) — behind this one interface, so consumers like
    /// <c>SetNet.PathFinding</c> and <c>SetNet.Mobs</c> don't care which the world was baked as.
    /// </summary>
    /// <remarks>All queries are read-only and thread-safe; the geometry is immutable after baking/loading.</remarks>
    public interface IGeoData
    {
        /// <summary>The world-space bounds of the geometry.</summary>
        Bounds Bounds { get; }

        /// <summary>True if an agent can stand at (or just above) <paramref name="point"/>.</summary>
        bool IsWalkable(Vec3 point);

        /// <summary>True if nothing blocks a straight line of sight from <paramref name="from"/> to <paramref name="to"/> (for vision/aim).</summary>
        bool LineOfSight(Vec3 from, Vec3 to);

        /// <summary>True if an agent could walk in a straight line from <paramref name="from"/> to <paramref name="to"/> staying on walkable ground the whole way (for movement/steering).</summary>
        bool CanWalkStraight(Vec3 from, Vec3 to);

        /// <summary>Casts a ray and returns the first blocking hit within <paramref name="maxDistance"/> (or <see cref="RaycastHit.None"/>).</summary>
        RaycastHit Raycast(Vec3 origin, Vec3 direction, float maxDistance);

        /// <summary>Returns the nearest walkable position to <paramref name="point"/> (snaps an off-mesh/blocked point onto the geometry).</summary>
        Vec3 SampleNearestWalkable(Vec3 point);

        /// <summary>Returns the ground height (world Y) under <paramref name="point"/>'s XZ, or <see cref="float.NaN"/> if there is none there.</summary>
        float SampleHeight(Vec3 point);
    }
}
