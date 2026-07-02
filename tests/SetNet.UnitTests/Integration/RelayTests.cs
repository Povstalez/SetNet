using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Relay;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test for the relay hub: allocate/join by code and forward opaque bytes between peers.</summary>
[Collection("integration")]
public class RelayTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Allocate_Join_And_Forward()
    {
        var server = new TestServer(Config("relay"));
        server.UseRelay();
        _ = server.StartAsync();
        await Task.Delay(120);

        // Host allocates a session.
        var host = new TestClient(Config("relay"));
        var relayHost = host.UseRelay();
        uint? joined = null;
        var hostGot = new ConcurrentQueue<(uint from, byte[] data)>();
        relayHost.PeerJoined += id => joined = id;
        relayHost.Received += (from, data) => hostGot.Enqueue((from, data));
        await host.ConnectAsync();
        var code = await relayHost.AllocateAsync(maxPeers: 4);
        Assert.False(string.IsNullOrEmpty(code));

        // Guest joins by code.
        var guest = new TestClient(Config("relay"));
        var relayGuest = guest.UseRelay();
        await guest.ConnectAsync();
        await relayGuest.JoinAsync(code);

        Assert.True(await WaitUntil(() => joined == relayGuest.OwnId));   // host saw the guest join

        // Guest forwards opaque bytes → host receives them tagged with the guest's peer id.
        await relayGuest.SendAsync(new byte[] { 7, 8, 9 });
        Assert.True(await WaitUntil(() => !hostGot.IsEmpty));
        Assert.Contains(hostGot, m => m.from == relayGuest.OwnId && m.data.Length == 3 && m.data[0] == 7 && m.data[2] == 9);

        host.Disconnect(); guest.Disconnect();
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
