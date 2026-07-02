using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.InMemory;
using SetNet.Multiplex;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for logical channels: multiplexed sends reach the original typed handlers in both
/// directions, and frames within one channel keep their order.
/// </summary>
[Collection("integration")]
public class MultiplexTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Mux_Frames_Reach_Original_Handlers_Both_Ways()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("mux"));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("mux"));
        client.UseMultiplex();
        await client.ConnectAsync();

        // Client → server on a channel: the normal echo handler (type 900) must fire and echo back (type 901);
        // the echo reply arrives unmuxed, proving the server handler saw the original message.
        await client.SendMuxAsync(channel: 3, (ushort)900, new EchoMessage { Text = "via-mux" });
        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("via-mux")));
        Assert.True(await WaitUntil(() => TestInbox.ClientReceived.Contains("via-mux")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Frames_Within_A_Channel_Stay_Ordered()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("mux-order"));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("mux-order"));
        client.UseMultiplex();
        await client.ConnectAsync();

        const int count = 50;
        for (var i = 0; i < count; i++)
            await client.SendMuxAsync(channel: 7, (ushort)900, new EchoMessage { Text = $"m{i:D3}" });

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= count));

        // The per-channel lane must preserve arrival order end to end.
        var received = TestInbox.ServerReceived.Where(t => t.StartsWith("m")).ToList();
        var expected = Enumerable.Range(0, count).Select(i => $"m{i:D3}").ToList();
        Assert.Equal(expected, received);

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
