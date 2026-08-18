using System.Linq;
using SetNet.GeoData;
using SetNet.Locomotion;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>
/// The route a <see cref="Mover"/> walks is readable from outside.
///
/// <para>
/// This exists so a server can publish the polyline it already computed instead of replicating a bare destination
/// and making every client re-run the same search. The tests therefore check the two things a replicating server
/// depends on: that the waypoints really are the ones being walked, and that they end at the destination.
/// </para>
/// </summary>
public class MoverRouteTests
{
    private static GridGeoData Field() =>
        new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 20, depth: 20)
            .Fill((_, _) => (true, false, 0f)).Build();

    /// <summary>A wall across the middle with one gap — the case where the route is worth sending at all.</summary>
    private static GridGeoData Walled() =>
        new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 20, depth: 20)
            .Fill((x, z) => (z != 10 || x == 5, false, 0f)).Build();

    [Fact]
    public void Idle_Mover_Has_No_Route()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 8f);

        Assert.Empty(m.Waypoints);
        Assert.Equal(0, m.WaypointIndex);
        Assert.Equal(1, loco.Count);
        Assert.Equal(0, loco.ActiveCount);
    }

    [Fact]
    public void Only_Movers_With_A_Live_Route_Are_In_The_Tick_Set()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });
        var idle = Enumerable.Range(0, 10_000)
            .Select(_ => loco.CreateMover(new Vec3(2, 0, 2), speed: 8f))
            .ToArray();
        var moving = idle[1234];

        Assert.Equal(10_000, loco.Count);
        Assert.Equal(0, loco.ActiveCount);

        Assert.True(moving.GoTo(new Vec3(16, 0, 2)));
        Assert.Equal(1, loco.ActiveCount);

        // Stationary movers remain registered for future orders, but stopping
        // the only live route makes the next Update independent of all 10,000.
        moving.Stop();
        Assert.Equal(10_000, loco.Count);
        Assert.Equal(0, loco.ActiveCount);
    }

    [Fact]
    public void Arrived_Warped_And_Disposed_Movers_Leave_The_Active_Set()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 100f);

        Assert.True(m.GoTo(new Vec3(3, 0, 2)));
        Assert.Equal(1, loco.ActiveCount);
        loco.Update(1_000);
        Assert.False(m.IsMoving);
        Assert.Equal(0, loco.ActiveCount);

        Assert.True(m.GoTo(new Vec3(16, 0, 2)));
        m.Warp(new Vec3(4, 0, 2));
        Assert.Equal(0, loco.ActiveCount);

        Assert.True(m.GoTo(new Vec3(16, 0, 2)));
        m.Dispose();
        Assert.Equal(0, loco.ActiveCount);
        Assert.Equal(0, loco.Count);
    }

    [Fact]
    public void Route_Is_Published_On_GoTo_And_Ends_At_The_Destination()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 8f);

        Assert.True(m.GoTo(new Vec3(16, 0, 2)));

        Assert.NotEmpty(m.Waypoints);

        // The last waypoint is where the mover will actually stop. A server that sends the route instead of the
        // ordered point relies on exactly this: client and server must agree on the end, not merely on the intent.
        var last = m.Waypoints[m.Waypoints.Count - 1];
        Assert.True((last - m.Destination!.Value).Length < 1.5f,
                    $"route should end at the destination, ended at {last}");
    }

    [Fact]
    public void Route_Is_The_One_Actually_Walked()
    {
        // The point of the property: not "a" path, but *the* path this mover follows. Walk it and check the
        // positions stay near the published polyline — if the mover ever used a different route internally, this
        // is where that would show.
        var loco = new LocomotionSystem(Walled(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 6f);

        Assert.True(m.GoTo(new Vec3(16, 0, 18)));
        var route = m.Waypoints.ToArray();
        Assert.True(route.Length >= 3, "a wall with one gap must bend the route");

        for (var i = 0; i < 200 && m.IsMoving; i++)
        {
            loco.Update(50);
            Assert.True(NearRoute(m.Position, route),
                        $"position {m.Position} drifted off the published route");
        }
    }

    [Fact]
    public void WaypointIndex_Advances_As_The_Mover_Walks()
    {
        var loco = new LocomotionSystem(Walled(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 6f);

        Assert.True(m.GoTo(new Vec3(16, 0, 18)));
        Assert.Equal(0, m.WaypointIndex);

        for (var i = 0; i < 20 && m.IsMoving; i++) loco.Update(50);

        // Needed by the "client joined mid-walk" case: the remainder is Waypoints[WaypointIndex..].
        Assert.True(m.WaypointIndex > 0, "index should move once waypoints are consumed");
    }

    private static bool NearRoute(Vec3 p, Vec3[] route)
    {
        for (var i = 1; i < route.Length; i++)
            if (DistanceToSegment(p, route[i - 1], route[i]) < 0.75f) return true;
        return false;
    }

    private static float DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z;
        if (len2 <= 1e-6f) return (p - a).Length;

        var ap = p - a;
        var t = (ap.X * ab.X + ap.Y * ab.Y + ap.Z * ab.Z) / len2;
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        return (p - (a + ab * t)).Length;
    }
}
