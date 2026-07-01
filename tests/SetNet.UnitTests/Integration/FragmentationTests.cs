using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Fragmentation;
using SetNet.InMemory;
using SetNet.Messaging;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test that a message larger than one chunk is split and reassembled into the original.</summary>
[Collection("integration")]
public class FragmentationTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Large_Message_Is_Fragmented_And_Reassembled()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("frag"));
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("frag"));
        client.UseFragmentation();
        await client.ConnectAsync();

        var big = new string('x', 5000);                              // ~5 KB
        var bytes = SetNetSerializer.Serialize(new EchoMessage { Text = big });
        Assert.True(bytes.Length > 200);                              // will span many fragments at maxChunk=200
        await client.SendFragmentedAsync(900, bytes, DeliveryMethod.Reliable, maxChunk: 200);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains(big)));   // reassembled + dispatched to the 900 handler

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
