using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end transport tests that bind real loopback sockets and exercise the full
/// send → frame → transport → dispatch → handler → echo path for each transport mode.
/// </summary>
[Collection("integration")]
public class TransportIntegrationTests
{
    private static Configuration Config(int port, TransportType transport,
        DeliveryMethod delivery = DeliveryMethod.Reliable, int lossPercent = 0) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = transport,
        DefaultDelivery = delivery,
        UdpSimulatedLossPercent = lossPercent,
        UdpReliableAckTimeoutMs = 50
    };

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Tcp_Echo_RoundTrips()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5821, TransportType.Tcp));
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5821, TransportType.Tcp));
        await client.ConnectAsync();
        await client.SendEchoAsync("hello", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("hello") && TestInbox.ClientReceived.Contains("hello")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task UdpReliable_Echo_RoundTrips()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5822, TransportType.Udp));
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5822, TransportType.Udp));
        await client.ConnectAsync();
        await client.SendEchoAsync("ping", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("ping") && TestInbox.ClientReceived.Contains("ping")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task UdpReliable_UnderSimulatedLoss_DeliversAll()
    {
        const int count = 20;
        TestInbox.Reset();
        var server = new TestServer(Config(5823, TransportType.Udp, DeliveryMethod.Reliable, lossPercent: 30));
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5823, TransportType.Udp, DeliveryMethod.Reliable, lossPercent: 30));
        await client.ConnectAsync();
        for (int i = 0; i < count; i++)
            await client.SendEchoAsync("m" + i, DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= count, 10000),
            $"server received {TestInbox.ServerReceived.Count}/{count}");
        // Reliable + ordered ⇒ each message delivered exactly once despite 30% loss.
        Assert.Equal(count, TestInbox.ServerReceived.Distinct().Count());

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Both_ReliableMessage_ArrivesOverTcp()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5824, TransportType.Both));
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5824, TransportType.Both));
        await client.ConnectAsync();
        await Task.Delay(400); // allow the UDP channel to attach
        await client.SendEchoAsync("reliable", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("reliable")));

        client.Disconnect();
        await server.StopAsync();
    }
}
