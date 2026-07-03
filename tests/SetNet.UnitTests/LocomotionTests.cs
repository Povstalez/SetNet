using System.Linq;
using SetNet.GeoData;
using SetNet.Locomotion;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for SetNet.Locomotion: the unified server-side movement tick (auto-subscribe, no networking).</summary>
public class LocomotionTests
{
    private static GridGeoData Field() =>
        new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 20, depth: 20)
            .Fill((_, _) => (true, false, 0f)).Build();

    [Fact]
    public void CreateMover_AutoSubscribes_TicksToDestination_AndDisposeUnsubscribes()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });

        var started = 0;
        loco.Started += _ => started++;

        var m = loco.CreateMover(new Vec3(2, 0, 2), speed: 8f, owner: "hero");
        Assert.Equal(1, loco.Count);                 // auto-subscribed just by creating it

        Assert.True(m.GoTo(new Vec3(16, 0, 2)));
        Assert.Equal(1, started);                    // Started fired on GoTo (the hook to send the point)
        Assert.True(m.IsMoving);

        var reached = false;
        m.DestinationReached += _ => reached = true;
        for (var i = 0; i < 40 && m.IsMoving; i++) loco.Update(100);   // 4s of 100ms ticks

        Assert.True(m.Position.X > 13f, $"should have advanced toward x=16, got {m.Position.X}");
        Assert.True(reached);
        Assert.False(m.IsMoving);

        m.Dispose();
        Assert.Equal(0, loco.Count);                 // auto-unsubscribe
    }

    [Fact]
    public void ManyMovers_AdvanceTogether_InOneTick()
    {
        var loco = new LocomotionSystem(Field(), new LocomotionOptions { UseInternalTimer = false });
        var movers = Enumerable.Range(0, 5)
            .Select(i => { var mv = loco.CreateMover(new Vec3(2, 0, 2 + i), 6f); mv.GoTo(new Vec3(15, 0, 2 + i)); return mv; })
            .ToList();

        Assert.Equal(5, loco.Count);
        for (var i = 0; i < 40; i++) loco.Update(100);
        Assert.All(movers, mv => Assert.True(mv.Position.X > 12f));   // all advanced by the single unified tick
    }

    [Fact]
    public void GoTo_Unreachable_ReturnsFalse_AndStaysIdle()
    {
        var b = new GridGeoDataBuilder(Vec3.Zero, 1f, 6, 6).Fill((_, _) => (true, false, 0f));
        foreach (var (x, z) in new[] { (3, 4), (5, 4), (4, 3), (4, 5) }) b.SetBlocked(x, z);
        b.SetWalkable(4, 4);                          // island
        var loco = new LocomotionSystem(b.Build(), new LocomotionOptions { UseInternalTimer = false });
        var m = loco.CreateMover(new Vec3(0.5f, 0, 0.5f), 5f);

        Assert.False(m.GoTo(new Vec3(4.5f, 0, 4.5f)));
        Assert.False(m.IsMoving);
    }
}
