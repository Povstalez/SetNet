using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using SetNet.StateSync;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for SetNet.StateSync (spawn/despawn, delta snapshots, ownership, input, interest) over the in-memory transport.</summary>
[Collection("integration")]
public class StateSyncTests
{
    private const ushort PlayerArch = 1;

    static StateSyncTests()
    {
        // Registered once for the process; identical on both ends (same process here).
        ReplicaRegistry.Register(ReplicaSchema.Create(PlayerArch)
            .Field(FieldType.Vector3, interpolate: true)   // 0: position
            .Field(FieldType.Float, interpolate: true)     // 1: health
            .Field(FieldType.Int)                          // 2: score (discrete)
            .Build());
    }

    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Spawn_Move_And_Despawn_Replicate()
    {
        var server = new TestServer(Config("ss-move"));
        var world = server.UseStateSync(new StateSyncOptions { TickRate = 60 });
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("ss-move"));
        var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 0 });   // snap to latest, deterministic
        await client.ConnectAsync();
        await Task.Delay(100);   // let the observer register (auto-observe on connect)

        var e = world.Spawn(PlayerArch);
        e.SetVec3(0, new Vec3(1, 2, 3));
        e.SetFloat(1, 100f);
        e.SetInt(2, 7);

        Assert.True(await WaitUntil(() => repl.Entities.Any()));
        repl.Update();
        var view = repl.Entities.First();
        Assert.Equal(new Vec3(1, 2, 3), view.GetVec3(0));
        Assert.Equal(100f, view.GetFloat(1), 3);
        Assert.Equal(7, view.GetInt(2));

        // Move (delta path after the first full snapshot).
        e.SetVec3(0, new Vec3(4, 5, 6));
        Assert.True(await WaitUntil(() => { repl.Update(); return Approx(view.GetVec3(0), new Vec3(4, 5, 6)); }));

        // Despawn.
        world.Despawn(e);
        Assert.True(await WaitUntil(() => !repl.Entities.Any()));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Ownership_Is_Flagged_For_The_Owning_Client()
    {
        BasePeer? peer = null;
        var server = new TestServer(Config("ss-owner"));
        server.PeerConnected += p => peer = p;
        var world = server.UseStateSync(new StateSyncOptions { TickRate = 60 });
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("ss-owner"));
        var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 0 });
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => peer != null));

        var owned = world.Spawn(PlayerArch, peer!.CurrentPeerInfo.Id);
        var other = world.Spawn(PlayerArch);   // server-owned

        Assert.True(await WaitUntil(() => repl.Entities.Count() == 2));
        Assert.True(await WaitUntil(() => repl.OwnedEntity != null));
        Assert.Equal(owned.NetId, repl.OwnedEntity!.NetId);
        Assert.Contains(repl.Entities, v => v.NetId == other.NetId && !v.IsOwner);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Input_Reaches_Server_And_Is_Acknowledged()
    {
        var server = new TestServer(Config("ss-input"));
        var world = server.UseStateSync(new StateSyncOptions { TickRate = 60 });
        var received = new List<byte[]>();
        world.InputReceived += (p, seq, payload) => received.Add(payload);
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("ss-input"));
        var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 0 });
        await client.ConnectAsync();
        await Task.Delay(100);

        var seq = repl.SendInput(new byte[] { 9, 8, 7 });
        Assert.True(await WaitUntil(() => received.Count > 0));
        Assert.Equal(new byte[] { 9, 8, 7 }, received[0]);
        Assert.True(await WaitUntil(() => repl.LastProcessedInput >= seq));   // server echoes the processed input in snapshots

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Interest_Hides_Distant_Entities()
    {
        var server = new TestServer(Config("ss-interest"));
        var options = new StateSyncOptions
        {
            TickRate = 60,
            Interest = new DistanceInterest(
                entityPosition: e => e.GetVec3(0),
                observerPosition: _ => Vec3.Zero,   // this observer's focus is the origin
                radius: 10f,
                alwaysSeeOwnedEntities: false),
        };
        var world = server.UseStateSync(options);
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("ss-interest"));
        var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 0 });
        await client.ConnectAsync();
        await Task.Delay(100);

        var near = world.Spawn(PlayerArch); near.SetVec3(0, new Vec3(1, 0, 0));    // inside radius
        var far = world.Spawn(PlayerArch); far.SetVec3(0, new Vec3(100, 0, 0));    // outside radius

        Assert.True(await WaitUntil(() => repl.Entities.Any(v => v.NetId == near.NetId)));
        await Task.Delay(150);   // give the far one a chance to (wrongly) appear
        Assert.DoesNotContain(repl.Entities, v => v.NetId == far.NetId);

        // Move the far entity into range → it should spawn.
        far.SetVec3(0, new Vec3(2, 0, 0));
        Assert.True(await WaitUntil(() => repl.Entities.Any(v => v.NetId == far.NetId)));

        client.Disconnect();
        await server.StopAsync();
    }

    private static bool Approx(Vec3 a, Vec3 b, float eps = 0.01f)
        => Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps && Math.Abs(a.Z - b.Z) < eps;

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
