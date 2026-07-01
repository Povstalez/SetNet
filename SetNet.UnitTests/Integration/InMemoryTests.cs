using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.InMemory;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test of the in-memory loopback transport (custom-transport hook): a full echo round-trip with no sockets.</summary>
[Collection("integration")]
public class InMemoryTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task InMemory_Echo_RoundTrips()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("inmem-echo"));
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("inmem-echo"));
        await client.ConnectAsync();
        await client.SendEchoAsync("mem-hello", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("mem-hello") && TestInbox.ClientReceived.Contains("mem-hello")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Connect_Without_Listener_Throws()
    {
        var client = new TestClient(Config("inmem-nolistener"));
        // No server bound to this key → ConnectAsync surfaces the failure.
        await Assert.ThrowsAnyAsync<System.Exception>(() => client.ConnectAsync());
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
