using System;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.GeoData;
using SetNet.Mobs;
using SetNet.NPC;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end smoke for SetNet.NPC (interact → capability hand-off) over the unified protocol.</summary>
[Collection("integration")]
public class NpcTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task Interact_ReturnsCapabilityHandoff()
    {
        var server = new TestServer(Config(5961));
        var npc = server.UseNpc();
        npc.Register(new VendorNpcBehaviour("blacksmith"));      // NpcType "vendor"
        var id = npc.Spawn(new NpcSpawn { Type = "vendor", Zone = "town", Position = new Vec3(0, 0, 0) });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5961));
        var npcClient = client.UseNpc();
        await client.ConnectAsync();

        await npcClient.EnterZoneAsync("town");                  // discover instances in the zone
        Assert.Contains(npcClient.Nearby, i => i.Id == id);

        var resp = await npcClient.InteractAsync(id, "open");
        Assert.True(resp.Ok);
        Assert.Equal("vendor:blacksmith", resp.Capability);      // capability hand-off

        client.Disconnect();
        await server.StopAsync();
    }
}

/// <summary>Server-side smoke for SetNet.Mobs AI: an aggressive mob perceives a nearby player, targets it, and moves toward it — no StateSync needed.</summary>
[Collection("integration")]
public class MobsTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task AggressiveMob_Targets_And_MovesTowardPlayer()
    {
        var server = new TestServer(Config(5962));   // no network needed — we drive the tick directly
        var mobs = server.UseMobs(new MobOptions
        {
            UseInternalTimer = false,                 // we call Update() ourselves
            MoveSpeed = 4f,
            PlayerPosition = key => key == "p1" ? new Vec3(10, 0, 0) : (Vec3?)null,
            AllPlayers = () => new[] { "p1" },
        });
        mobs.Register(new AggressiveBrain("goblin", aggroRadius: 20f, attackRange: 1f));
        var id = mobs.Spawn(new MobSpawn { Type = "goblin", Position = new Vec3(0, 0, 0), Health = 100 });

        for (var i = 0; i < 12; i++) await mobs.Update(100);   // 12 × 100 ms ticks

        var mob = mobs.Mobs.First(m => m.Id == id);
        Assert.Equal("p1", mob.Target);                        // aggroed onto the player
        Assert.True(mob.Position.X > 0.5f, $"mob should have moved toward the player at x=10, got x={mob.Position.X}");
    }
}
