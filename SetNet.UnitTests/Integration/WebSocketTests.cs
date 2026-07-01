using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.WebSockets;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test of the WebSocket transport (custom-transport hook): a full echo round-trip over WS.</summary>
[Collection("integration")]
public class WebSocketTests
{
    private static Configuration Config(int port)
    {
        var c = new Configuration { Host = "127.0.0.1", Port = port };
        c.UseWebSockets();   // TransportType.Custom + WebSocketTransport
        return c;
    }

    [Fact]
    public async Task WebSocket_Echo_RoundTrips()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5901));
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new TestClient(Config(5901));
        await client.ConnectAsync();
        await client.SendEchoAsync("ws-hello", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("ws-hello") && TestInbox.ClientReceived.Contains("ws-hello")));

        client.Disconnect();
        await server.StopAsync();
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
