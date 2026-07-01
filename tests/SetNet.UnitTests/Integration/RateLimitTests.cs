using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.RateLimit;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test of per-peer inbound rate limiting.</summary>
[Collection("integration")]
public class RateLimitTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task Over_Budget_Frames_Are_Dropped()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5911));
        server.UseRateLimit(new RateLimitOptions { PerPeerPerSecond = 5, Burst = 3 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5911));
        await client.ConnectAsync();

        const int sent = 40;
        for (var i = 0; i < sent; i++)
            await client.SendEchoAsync("m" + i, DeliveryMethod.Reliable);

        await Task.Delay(400);   // let anything admitted arrive

        // With burst 3 + ~5/s over a sub-second window, far fewer than everything gets through.
        var received = TestInbox.ServerReceived.Count;
        Assert.InRange(received, 1, sent - 1);   // some delivered, but not all (rate-limited)

        client.Disconnect();
        await server.StopAsync();
    }
}
