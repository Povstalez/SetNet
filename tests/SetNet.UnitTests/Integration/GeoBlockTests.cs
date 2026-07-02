using System;
using System.Net;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.GeoBlock;
using SetNet.InMemory;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for the geo-block gate. The in-memory transport has no remote IP, so the resolver returns null
/// and the <c>BlockUnknown</c> path drives the decision — which exercises the kick-on-connect behaviour.
/// </summary>
[Collection("integration")]
public class GeoBlockTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    /// <summary>A resolver that never knows the country (as with the IP-less in-memory transport).</summary>
    private sealed class NullResolver : IGeoResolver { public string? CountryOf(IPAddress address) => null; }

    [Fact]
    public async Task Unknown_Country_Is_Kicked_When_BlockUnknown()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("geo-block"));
        var blocked = false;
        var geo = server.UseGeoBlock(new NullResolver(), new GeoBlockOptions { BlockUnknown = true });
        geo.Blocked += (_, _) => blocked = true;
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("geo-block"));
        await client.ConnectAsync();

        Assert.True(await WaitUntil(() => blocked));            // kicked on connect
        await Task.Delay(120);
        try { await client.SendEchoAsync("hi", DeliveryMethod.Reliable); } catch { /* connection gone */ }
        await Task.Delay(200);

        Assert.Empty(TestInbox.ServerReceived);                // never processed

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Unknown_Country_Passes_When_Allowed()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("geo-pass"));
        server.UseGeoBlock(new NullResolver(), new GeoBlockOptions { BlockUnknown = false });
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("geo-pass"));
        await client.ConnectAsync();
        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("hi")));

        client.Disconnect();
        await server.StopAsync();
    }

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
