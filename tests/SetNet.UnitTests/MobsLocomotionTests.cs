using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Mobs;
using SetNet.Mobs.Locomotion;
using SetNet.UnitTests.Integration;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Verifies SetNet.Mobs.Locomotion: mob movement is advanced by the shared LocomotionSystem when opted in.</summary>
public class MobsLocomotionTests
{
    [Fact]
    public async Task Mob_MovesThroughLocomotion_WhenMoverIsSet()
    {
        var geo = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 40, depth: 40)
            .Fill((_, _) => (true, false, 0f)).Build();
        var loco = new LocomotionSystem(geo, new LocomotionOptions { UseInternalTimer = false });

        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 6141, TransportType = TransportType.Tcp });
        var mobs = server.UseMobs(new MobOptions
        {
            UseInternalTimer = false,
            MoveSpeed = 8f,
            GeoData = geo,
            PlayerPosition = key => key == "p1" ? new Vec3(30, 0, 2) : (Vec3?)null,
            AllPlayers = () => new[] { "p1" },
            Mover = loco.AsMobMover(),          // ← delegate mob movement to the unified Locomotion tick
        });
        mobs.Register(new AggressiveBrain("wolf", aggroRadius: 60f, attackRange: 1f, leashRadius: 100f, requireLos: false));
        var id = mobs.Spawn(new MobSpawn { Type = "wolf", Position = new Vec3(2, 0, 2), Health = 100 });

        // Mobs decides (SetGoal on the mover); Locomotion advances the position.
        for (var i = 0; i < 40; i++) { await mobs.Update(100); loco.Update(100); }

        var mob = mobs.Mobs.First(m => m.Id == id);
        Assert.Equal("p1", mob.Target);         // aggroed onto the player
        Assert.True(mob.Position.X > 15f, $"mob should have moved toward the player via Locomotion, got x={mob.Position.X}");
    }
}
