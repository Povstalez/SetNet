using System.IO;
using System.Linq;
using SetNet.GeoData;
using SetNet.PathFinding;
using Xunit;
using Path = SetNet.PathFinding.Path;

namespace SetNet.UnitTests;

/// <summary>Unit tests for SetNet.GeoData (grid + nav-mesh queries, file round-trip) and SetNet.PathFinding (A* + follower).</summary>
public class GeoDataPathfindingTests
{
    // A 10x10 walkable grid with a vertical wall at x=5 for z=0..7 (a gap at z=8,9).
    private static GridGeoData WalledGrid() =>
        new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 10, depth: 10)
            .Fill((cx, cz) => cx == 5 && cz <= 7 ? (false, true, 0f) : (true, false, 0f))
            .Build();

    [Fact]
    public void Grid_LineOfSight_And_CanWalkStraight_RespectWall()
    {
        var g = WalledGrid();
        var a = new Vec3(1.5f, 0, 1.5f);
        var b = new Vec3(8.5f, 0, 1.5f);   // straight line crosses the x=5 wall
        Assert.False(g.LineOfSight(a, b));
        Assert.False(g.CanWalkStraight(a, b));

        var open = new Vec3(3.5f, 0, 1.5f); // same side, no wall between
        Assert.True(g.LineOfSight(a, open));
        Assert.True(g.CanWalkStraight(a, open));

        Assert.True(g.IsWalkable(a));
        Assert.False(g.IsWalkable(new Vec3(5.5f, 0, 1.5f)));   // on the wall
    }

    [Fact]
    public void Grid_Pathfinder_RoutesAroundWall()
    {
        var g = WalledGrid();
        var finder = Pathfinding.For(g);
        var path = finder.FindPath(new Vec3(1.5f, 0, 1.5f), new Vec3(8.5f, 0, 1.5f));

        Assert.False(path.IsEmpty);
        Assert.True(path.Waypoints.Count >= 2);
        Assert.True(path.Length > 7f);   // longer than the straight-line 7 because it detours through the gap
        // Endpoints line up with the request.
        Assert.Equal(1.5f, path.Waypoints[0].X, 3);
        Assert.Equal(8.5f, path.Waypoints[^1].X, 3);
        // Every waypoint is on walkable ground.
        Assert.All(path.Waypoints, w => Assert.True(g.IsWalkable(w)));
    }

    [Fact]
    public void Grid_Pathfinder_NoPath_WhenGoalUnreachable_IsFine()
    {
        // Fully wall off a 1-cell island around the goal.
        var b = new GridGeoDataBuilder(Vec3.Zero, 1f, 6, 6).Fill((x, z) => (true, false, 0f));
        foreach (var (x, z) in new[] { (3, 4), (5, 4), (4, 3), (4, 5) }) b.SetBlocked(x, z);
        b.SetWalkable(4, 4);   // island at (4,4), surrounded by walls
        var g = b.Build();

        var path = Pathfinding.For(g).FindPath(new Vec3(0.5f, 0, 0.5f), new Vec3(4.5f, 0, 4.5f));
        Assert.True(path.IsEmpty);   // unreachable → empty, not a crash
    }

    [Fact]
    public void Grid_FileRoundTrip_PreservesQueries()
    {
        var g = WalledGrid();
        using var ms = new MemoryStream();
        GeoDataFile.Save(g, ms);
        ms.Position = 0;
        var loaded = GeoDataFile.Load(ms);

        Assert.IsType<GridGeoData>(loaded);
        Assert.True(loaded.IsWalkable(new Vec3(1.5f, 0, 1.5f)));
        Assert.False(loaded.IsWalkable(new Vec3(5.5f, 0, 1.5f)));
        Assert.False(loaded.LineOfSight(new Vec3(1.5f, 0, 1.5f), new Vec3(8.5f, 0, 1.5f)));
    }

    // A flat 4x4 quad split into two triangles.
    private static NavMeshGeoData FlatQuad()
    {
        var verts = new[] { new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(4, 0, 4), new Vec3(0, 0, 4) };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        return NavMeshGeoData.FromTriangles(verts, tris);
    }

    [Fact]
    public void NavMesh_Queries_Work()
    {
        var m = FlatQuad();
        Assert.Equal(2, m.TriangleCount);
        Assert.True(m.IsWalkable(new Vec3(1, 0, 1)));
        Assert.False(m.IsWalkable(new Vec3(10, 0, 10)));
        Assert.Equal(0f, m.SampleHeight(new Vec3(2, 0, 2)), 3);

        var snapped = m.SampleNearestWalkable(new Vec3(10, 0, 2));
        Assert.True(m.IsWalkable(snapped) || Vec3.HorizontalDistance(snapped, new Vec3(4, 0, 2)) < 0.6f);
    }

    [Fact]
    public void NavMesh_Pathfinder_FindsPathAcrossTriangles()
    {
        var m = FlatQuad();
        var path = Pathfinding.For(m).FindPath(new Vec3(3.5f, 0, 0.5f), new Vec3(0.5f, 0, 3.5f));
        Assert.False(path.IsEmpty);
        Assert.True(path.Length > 0f);
    }

    // Two flat 4x4 floors stacked at the SAME XZ: ground at Y=0, upper storey at Y=3.
    private static NavMeshGeoData StackedFloors()
    {
        var verts = new[]
        {
            new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(4, 0, 4), new Vec3(0, 0, 4),   // ground
            new Vec3(0, 3, 0), new Vec3(4, 3, 0), new Vec3(4, 3, 4), new Vec3(0, 3, 4),   // upper
        };
        var tris = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        return NavMeshGeoData.FromTriangles(verts, tris);
    }

    [Fact]
    public void NavMesh_MultiStorey_ResolvesFloorByHeight()
    {
        var m = StackedFloors();

        // A point at ground height resolves to the ground floor; one at upper height to the upper storey.
        Assert.Equal(0f, m.SampleHeight(new Vec3(2, 0.1f, 2)), 2);
        Assert.Equal(3f, m.SampleHeight(new Vec3(2, 2.9f, 2)), 2);
        Assert.True(m.IsWalkable(new Vec3(2, 0f, 2)));
        Assert.True(m.IsWalkable(new Vec3(2, 3f, 2)));

        // You can walk straight within one floor …
        Assert.True(m.CanWalkStraight(new Vec3(0.5f, 0, 0.5f), new Vec3(3.5f, 0, 3.5f)));
        // … but not straight from the ground floor up to the storey above (no path through the air).
        Assert.False(m.CanWalkStraight(new Vec3(0.5f, 0, 0.5f), new Vec3(3.5f, 3, 3.5f)));
    }

    [Fact]
    public void NavMesh_FileRoundTrip_PreservesMesh()
    {
        var m = FlatQuad();
        using var ms = new MemoryStream();
        GeoDataFile.Save(m, ms);
        ms.Position = 0;
        var loaded = Assert.IsType<NavMeshGeoData>(GeoDataFile.Load(ms));
        Assert.Equal(2, loaded.TriangleCount);
        Assert.True(loaded.IsWalkable(new Vec3(1, 0, 1)));
    }

    // A two-storey layered grid: a ground room (x0..2, Y=0), a staircase (x3=Y1, x4=Y2, x5=Y3),
    // an upper landing (x6, Y=4), and an upper deck at Y=4 spanning x0..6 — so x0..5 hold TWO stacked
    // walkable surfaces (ground/stairs below, deck above) at the same XZ. Optionally omit the stairs.
    private static LayeredGridGeoData TwoStorey(bool withStairs)
    {
        var b = new LayeredGridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 7, depth: 3)
            .SetMaxStep(1.1f).SetLayerMatchTolerance(2f);
        for (var z = 0; z < 3; z++)
        {
            b.AddLayer(0, z, 0f); b.AddLayer(1, z, 0f); b.AddLayer(2, z, 0f);   // ground room
            if (withStairs) { b.AddLayer(3, z, 1f); b.AddLayer(4, z, 2f); b.AddLayer(5, z, 3f); }
            b.AddLayer(6, z, 4f);                                               // upper landing
            for (var x = 0; x < 7; x++) b.AddLayer(x, z, 4f);                   // upper deck (overlaps everything)
        }
        return b.Build();
    }

    [Fact]
    public void Layered_ResolvesFloorByHeight()
    {
        var g = TwoStorey(withStairs: true);
        // Same XZ, different Y → different storey.
        Assert.Equal(0f, g.SampleHeight(new Vec3(0.5f, 0.1f, 0.5f)), 2);   // near the ground
        Assert.Equal(4f, g.SampleHeight(new Vec3(0.5f, 3.8f, 0.5f)), 2);   // near the upper deck
        Assert.True(g.IsWalkable(new Vec3(0.5f, 0f, 0.5f)));
        Assert.True(g.IsWalkable(new Vec3(0.5f, 4f, 0.5f)));
    }

    [Fact]
    public void Layered_CanWalkStraight_And_LineOfSight_AreStoreyAware()
    {
        var g = TwoStorey(withStairs: true);
        // Straight across one floor is fine …
        Assert.True(g.CanWalkStraight(new Vec3(0.5f, 0f, 0.5f), new Vec3(2.5f, 0f, 0.5f)));
        // … but you can't walk straight from the ground up onto the deck through the air.
        Assert.False(g.CanWalkStraight(new Vec3(0.5f, 0f, 0.5f), new Vec3(0.5f, 4f, 0.5f)));
        // The upper deck occludes sight between the ground and a point above it.
        Assert.False(g.LineOfSight(new Vec3(0.5f, 0.1f, 0.5f), new Vec3(0.5f, 4.5f, 0.5f)));
        // Two points on the same floor can see each other.
        Assert.True(g.LineOfSight(new Vec3(0.5f, 0f, 0.5f), new Vec3(2.5f, 0f, 0.5f)));
    }

    [Fact]
    public void Layered_Pathfinder_ClimbsStairsBetweenFloors()
    {
        var g = TwoStorey(withStairs: true);
        var finder = Pathfinding.For(g);
        Assert.IsType<LayeredGridPathfinder>(finder);

        // From the ground room up to the deck directly above it: must detour through the staircase.
        var path = finder.FindPath(new Vec3(0.5f, 0f, 1.5f), new Vec3(0.5f, 4f, 1.5f));
        Assert.False(path.IsEmpty);
        Assert.Equal(4f, path.Waypoints[^1].Y, 1);                    // arrives on the upper storey
        Assert.True(path.Waypoints[0].Y < 1f);                       // started on the ground
        // The route reached the far landing (x≈6) before doubling back — i.e. it used the stairs.
        Assert.Contains(path.Waypoints, w => w.X > 5f);
    }

    [Fact]
    public void Layered_Pathfinder_NoStairs_FloorsAreUnreachable()
    {
        var g = TwoStorey(withStairs: false);   // ground and deck separated by a 4-unit gap everywhere
        var path = Pathfinding.For(g).FindPath(new Vec3(0.5f, 0f, 1.5f), new Vec3(0.5f, 4f, 1.5f));
        Assert.True(path.IsEmpty);              // no way up → empty, not a crash
    }

    [Fact]
    public void Layered_FileRoundTrip_PreservesFloors()
    {
        var g = TwoStorey(withStairs: true);
        using var ms = new MemoryStream();
        GeoDataFile.Save(g, ms);
        ms.Position = 0;
        var loaded = Assert.IsType<LayeredGridGeoData>(GeoDataFile.Load(ms));
        Assert.Equal(g.LayerCount, loaded.LayerCount);
        Assert.Equal(0f, loaded.SampleHeight(new Vec3(0.5f, 0.1f, 0.5f)), 2);
        Assert.Equal(4f, loaded.SampleHeight(new Vec3(0.5f, 3.8f, 0.5f)), 2);
        Assert.False(Pathfinding.For(loaded).FindPath(new Vec3(0.5f, 0f, 1.5f), new Vec3(0.5f, 4f, 1.5f)).IsEmpty);
    }

    [Fact]
    public void GridPathfinder_ReusedInstance_IsDeterministicAcrossManyQueries()
    {
        // The pooled, generation-stamped search state is reused across calls; a bug in the stamp reset would make
        // later queries diverge. Hammer one instance and assert every path is identical to the first.
        var g = WalledGrid();
        var finder = new GridPathfinder(g);
        var first = finder.FindPath(new Vec3(1.5f, 0, 1.5f), new Vec3(8.5f, 0, 1.5f));
        Assert.False(first.IsEmpty);
        for (var i = 0; i < 500; i++)
        {
            var p = finder.FindPath(new Vec3(1.5f, 0, 1.5f), new Vec3(8.5f, 0, 1.5f));
            Assert.Equal(first.Waypoints.Count, p.Waypoints.Count);
            Assert.Equal(first.Length, p.Length, 4);
        }
    }

    [Fact]
    public void GridPathfinder_MaxExpansions_BoundsTheSearch()
    {
        var g = WalledGrid();
        var finder = new GridPathfinder(g) { MaxExpansions = 1 };   // give up almost immediately
        var path = finder.FindPath(new Vec3(1.5f, 0, 1.5f), new Vec3(8.5f, 0, 1.5f));
        Assert.True(path.IsEmpty);   // capped out → empty rather than scanning the whole map
    }

    // Two 10x10 grids tiled edge-to-edge on X: sector A over x[0,10], sector B over x[10,20], both walkable.
    private static SectoredGeoData TwoSectorWorld()
    {
        GridGeoData Grid(float originX) => new GridGeoDataBuilder(new Vec3(originX, 0, 0), 1f, 10, 10)
            .Fill((_, _) => (true, false, 0f)).Build();
        return new SectoredGeoDataBuilder()
            .Add("A", Grid(0f), new Bounds(new Vec3(0, 0, 0), new Vec3(10, 0, 10)))
            .Add("B", Grid(10f), new Bounds(new Vec3(10, 0, 0), new Vec3(20, 0, 10)))
            .Build();
    }

    [Fact]
    public void Sectored_RoutesQueriesToTheOwningSector()
    {
        var world = TwoSectorWorld();
        Assert.True(world.TryGetSector(new Vec3(3, 0, 3), out var a));
        Assert.Equal("A", world.Sectors[a].Id);
        Assert.True(world.TryGetSector(new Vec3(15, 0, 3), out var b));
        Assert.Equal("B", world.Sectors[b].Id);

        Assert.True(world.IsWalkable(new Vec3(3, 0, 3)));      // in A
        Assert.True(world.IsWalkable(new Vec3(15, 0, 3)));     // in B
        Assert.False(world.IsWalkable(new Vec3(25, 0, 3)));    // outside every sector
        // A straight walk spanning the A|B border is allowed (both sides walkable, heights match).
        Assert.True(world.CanWalkStraight(new Vec3(3, 0, 3), new Vec3(17, 0, 3)));
        // Union bounds cover both sectors.
        Assert.Equal(0f, world.Bounds.Min.X, 3);
        Assert.Equal(20f, world.Bounds.Max.X, 3);
    }

    [Fact]
    public void Sectored_Pathfinder_CrossesSectorBorder()
    {
        var world = TwoSectorWorld();
        var finder = Pathfinding.For(world);
        Assert.IsType<SectoredPathfinder>(finder);

        var path = finder.FindPath(new Vec3(2.5f, 0, 2.5f), new Vec3(17.5f, 0, 2.5f));   // A -> B
        Assert.False(path.IsEmpty);
        Assert.Equal(2.5f, path.Waypoints[0].X, 1);
        Assert.Equal(17.5f, path.Waypoints[^1].X, 1);
        Assert.Contains(path.Waypoints, w => Math.Abs(w.X - 10f) < 1.5f);   // passed through the border
    }

    [Fact]
    public void Sectored_Manifest_RoundTripsThroughFiles()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "setnet_sectors_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Bake two sector files + a manifest that references them by relative path.
            GridGeoData Grid(float ox) => new GridGeoDataBuilder(new Vec3(ox, 0, 0), 1f, 10, 10).Fill((_, _) => (true, false, 0f)).Build();
            GeoDataFile.SaveToFile(Grid(0f), System.IO.Path.Combine(dir, "A.geo"));
            GeoDataFile.SaveToFile(Grid(10f), System.IO.Path.Combine(dir, "B.geo"));
            var entries = new[]
            {
                new GeoDataManifest.Entry("A", "A.geo", new Bounds(new Vec3(0, 0, 0), new Vec3(10, 0, 10))),
                new GeoDataManifest.Entry("B", "B.geo", new Bounds(new Vec3(10, 0, 0), new Vec3(20, 0, 10))),
            };
            var manifestPath = System.IO.Path.Combine(dir, "world.geomap");
            using (var fs = File.Create(manifestPath)) GeoDataManifest.Save(entries, fs);

            // Load the whole sectored world from the manifest and path across it.
            var world = GeoDataManifest.Load(manifestPath);
            Assert.Equal(2, world.Sectors.Count);
            Assert.False(Pathfinding.For(world).FindPath(new Vec3(2.5f, 0, 2.5f), new Vec3(17.5f, 0, 2.5f)).IsEmpty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PathFollower_WalksToTheEnd()
    {
        var path = new Path(new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 0, 10) });
        var follower = new PathFollower(path);
        var pos = new Vec3(0, 0, 0);
        // 200 ticks of 0.5 units each = 100 units, well over the 20-unit path.
        for (var i = 0; i < 200 && !follower.Arrived; i++) pos = follower.Step(pos, 0.5f);
        Assert.True(follower.Arrived);
        Assert.True(Vec3.Distance(pos, new Vec3(10, 0, 10)) < 0.01f);
    }
}
