using System;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Zones;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end test for seamless zone handoff. Origin and destination nodes share one handoff store (as they must
/// across processes in production); here the two in-memory servers share a MemoryHandoffStore instance.
/// </summary>
[Collection("integration")]
public class ZonesTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Handoff_Carries_State_To_Destination_Node()
    {
        var shared = new MemoryHandoffStore();   // both nodes read/write the same store

        // Origin node.
        var originCfg = Config("zone-origin");
        var origin = new TestServer(originCfg);
        var originZones = origin.UseZones(shared);
        SetNet.Core.BasePeer? originPeer = null;
        origin.PeerConnected += p => originPeer = p;
        _ = origin.StartAsync();

        // Destination node.
        var destCfg = Config("zone-dest");
        var dest = new TestServer(destCfg);
        dest.UseZones(shared);
        _ = dest.StartAsync();
        await Task.Delay(150);

        // Player connects to the origin.
        var originClient = new TestClient(originCfg);
        var originZonesClient = originClient.UseZones();
        ZoneTransfer? transfer = null;
        originZonesClient.TransferRequested += t => transfer = t;
        await originClient.ConnectAsync();
        Assert.True(await WaitUntil(() => originPeer != null));

        // Origin hands the player off to the destination with carried state.
        var carried = new byte[] { 1, 2, 3, 42 };
        await originZones.TransferAsync(originPeer!, new ZoneTarget("desert", "zone-dest", 1), carried);

        // Client is told to migrate.
        Assert.True(await WaitUntil(() => transfer != null));
        Assert.Equal("desert", transfer!.Target.ZoneId);
        Assert.Equal("zone-dest", transfer.Target.Host);

        // Client reconnects to the destination and claims its state.
        var destClient = new TestClient(destCfg);
        var destZonesClient = destClient.UseZones();
        await destClient.ConnectAsync();

        var restored = await destZonesClient.ClaimAsync(transfer.Token);
        Assert.Equal(carried, restored);

        // The token is one-time — a second claim fails.
        await Assert.ThrowsAsync<ZoneException>(() => destZonesClient.ClaimAsync(transfer.Token));

        originClient.Disconnect(); destClient.Disconnect();
        await origin.StopAsync(); await dest.StopAsync();
    }

    [Fact]
    public async Task Unknown_Token_Is_Rejected()
    {
        var config = Config("zone-unknown");
        var server = new TestServer(config);
        server.UseZones();
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var zones = client.UseZones();
        await client.ConnectAsync();

        await Assert.ThrowsAsync<ZoneException>(() => zones.ClaimAsync("does-not-exist"));

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}
