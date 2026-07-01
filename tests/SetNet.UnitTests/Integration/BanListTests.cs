using System.Threading.Tasks;
using SetNet.BanList;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.InMemory;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test of the ban gate (keyed by a fixed selector, since the in-memory transport has no IP).</summary>
[Collection("integration")]
public class BanListTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Banned_Key_Has_Its_Frames_Dropped()
    {
        TestInbox.Reset();
        var store = new MemoryBanStore();
        store.Ban("blocked");                              // pre-ban

        var server = new TestServer(Config("ban-drop"));
        server.UseBanList(_ => "blocked", store);          // every peer maps to the banned key
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("ban-drop"));
        await client.ConnectAsync();
        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);
        await Task.Delay(300);

        Assert.Empty(TestInbox.ServerReceived);            // gate dropped the frame

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Unbanned_Key_Passes()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("ban-pass"));
        server.UseBanList(_ => "ok", new MemoryBanStore());   // "ok" is not banned
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("ban-pass"));
        await client.ConnectAsync();
        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("hi")));

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(System.Func<bool> condition, int timeoutMs = 4000)
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
