using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// Server lifecycle-event guarantees over the in-memory transport: <c>PeerConnected</c> fires before the receive
/// loop (so a fast disconnect can't invert the order), and <c>PeerDisconnected</c> fires for live peers on a
/// graceful shutdown (so shared-store cleanup subscribers still run on a rolling restart).
/// </summary>
[Collection("integration")]
public class ServerLifecycleTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task PeerDisconnected_Fires_For_Live_Peers_On_Graceful_Stop()
    {
        var connected = 0;
        var disconnected = 0;
        var server = new TestServer(Config("lifecycle-stop"));
        server.PeerConnected += _ => Interlocked.Increment(ref connected);
        server.PeerDisconnected += _ => Interlocked.Increment(ref disconnected);
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("lifecycle-stop"));
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => Volatile.Read(ref connected) == 1));

        // A graceful StopAsync clears the peer pool then closes each peer; without firing the event explicitly during
        // teardown, PeerDisconnected would be silently skipped (RemoveClient finds the already-cleared pool).
        await server.StopAsync();
        Assert.Equal(1, Volatile.Read(ref disconnected));

        client.Disconnect();
    }

    [Fact]
    public async Task Connect_Disconnect_Events_Are_Matched_Never_Inverted()
    {
        var events = new ConcurrentQueue<(BasePeer Peer, bool Connected)>();
        var server = new TestServer(Config("lifecycle-churn"));
        server.PeerConnected += p => events.Enqueue((p, true));
        server.PeerDisconnected += p => events.Enqueue((p, false));
        _ = server.StartAsync();
        await Task.Delay(100);

        // Churn: connect then immediately disconnect — the race that could otherwise invert or orphan the events.
        // (TestServer starts the receive loop inside OnNewClient, so the peer can close before it's even announced.)
        for (var i = 0; i < 15; i++)
        {
            var c = new TestClient(Config("lifecycle-churn"));
            await c.ConnectAsync();
            c.Disconnect();
        }

        // A few stable clients guarantee some full connect→disconnect pairs are actually observed.
        var stable = new List<TestClient>();
        for (var i = 0; i < 5; i++)
        {
            var c = new TestClient(Config("lifecycle-churn"));
            await c.ConnectAsync();
            stable.Add(c);
        }
        Assert.True(await WaitUntil(() => events.Count(e => e.Connected) >= 5));
        await Task.Delay(300);   // let the churn disconnects settle

        // Invariants guaranteed by the peer lifecycle state machine: PeerConnected fires at most once per peer, and
        // PeerDisconnected only ever follows that peer's PeerConnected — never inverted, never orphaned. (A peer that
        // closed before it was announced fires neither event, so it simply never appears here.)
        var seenConnected = new HashSet<BasePeer>();
        foreach (var (peer, isConnected) in events)
        {
            if (isConnected) Assert.True(seenConnected.Add(peer), "PeerConnected fired more than once for a peer");
            else Assert.Contains(peer, seenConnected);
        }
        Assert.True(seenConnected.Count >= 5);

        foreach (var c in stable) c.Disconnect();
        await server.StopAsync();
    }

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
}
