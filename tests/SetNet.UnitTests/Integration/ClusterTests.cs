using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Cluster;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test for the cluster bus: two nodes mesh over TCP loopback and one publishes to the other.</summary>
[Collection("integration")]
public class ClusterTests
{
    [Fact]
    public async Task Published_Message_Reaches_Other_Node()
    {
        var a = new ClusterNode(new ClusterNodeOptions
        {
            NodeId = "a",
            ListenPort = 7411,
            Seeds = new[] { ("127.0.0.1", 7412) },
            ReconnectDelayMs = 200,
        });
        var b = new ClusterNode(new ClusterNodeOptions
        {
            NodeId = "b",
            ListenPort = 7412,
            Seeds = new[] { ("127.0.0.1", 7411) },
            ReconnectDelayMs = 200,
        });

        var got = new ConcurrentQueue<(string from, string topic, byte[] body)>();
        b.Received += (from, topic, body) => got.Enqueue((from, topic, body));

        await a.StartAsync();
        await b.StartAsync();
        await Task.Delay(800);                                  // let the mesh connect both ways

        await a.Publish("presence", new byte[] { 42, 7 });

        Assert.True(await WaitUntil(() => got.Count > 0, 8000));
        Assert.Contains(got, m => m.from == "a" && m.topic == "presence" && m.body.Length == 2 && m.body[0] == 42);

        await a.Stop();
        await b.Stop();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(30);
        }
        return condition();
    }
}
