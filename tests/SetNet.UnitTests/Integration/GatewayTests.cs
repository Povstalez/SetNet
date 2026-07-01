using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Gateway;
using SetNet.InMemory;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test that a client's traffic is relayed through the gateway to a backend and back.</summary>
[Collection("integration")]
public class GatewayTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Frames_Relay_Client_Through_Gateway_To_Backend_And_Back()
    {
        TestInbox.Reset();

        // Backend: a normal echo server.
        var backend = new TestServer(Config("gw-backend"));
        _ = backend.StartAsync();
        await Task.Delay(120);

        // Gateway: relays every client to the backend.
        var gateway = new GatewayServer(Config("gw-front"), _ => Config("gw-backend"));
        _ = gateway.StartAsync();
        await Task.Delay(120);

        // Client connects to the gateway, not the backend.
        var client = new TestClient(Config("gw-front"));
        await client.ConnectAsync();
        await client.SendEchoAsync("through-the-gateway", DeliveryMethod.Reliable);

        // The echo made a full round trip: client → gateway → backend (recorded) → gateway → client (recorded).
        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("through-the-gateway") &&
            TestInbox.ClientReceived.Contains("through-the-gateway")));

        client.Disconnect();
        await gateway.StopAsync();
        await backend.StopAsync();
    }

    private static async Task<bool> WaitUntil(System.Func<bool> condition, int timeoutMs = 5000)
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
