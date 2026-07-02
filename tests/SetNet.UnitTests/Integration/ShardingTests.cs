using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Sharding;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Unit tests for the consistent-hash ring plus an end-to-end test of the shard directory.</summary>
[Collection("integration")]
public class ShardingTests
{
    private static List<ShardNode> ThreeNodes() => new()
    {
        new ShardNode { NodeId = "n1", Host = "h1", Port = 5001 },
        new ShardNode { NodeId = "n2", Host = "h2", Port = 5002 },
        new ShardNode { NodeId = "n3", Host = "h3", Port = 5003 },
    };

    [Fact]
    public void Ring_Is_Deterministic_And_Covers_All_Nodes()
    {
        var a = new ShardRing(ThreeNodes());
        var b = new ShardRing(ThreeNodes());

        var owners = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            var key = $"key-{i}";
            // Same node list → same owner on any node/restart.
            Assert.Equal(a.GetNode(key)!.NodeId, b.GetNode(key)!.NodeId);
            owners.Add(a.GetNode(key)!.NodeId);
        }
        Assert.Equal(3, owners.Count);   // virtual nodes spread keys over every node
    }

    [Fact]
    public void Removing_A_Node_Remaps_Only_Its_Keys()
    {
        var full = new ShardRing(ThreeNodes());
        var reduced = new ShardRing(ThreeNodes().Where(n => n.NodeId != "n3"));

        var moved = 0;
        for (var i = 0; i < 1000; i++)
        {
            var key = $"key-{i}";
            var before = full.GetNode(key)!.NodeId;
            var after = reduced.GetNode(key)!.NodeId;
            if (before == "n3") Assert.NotEqual("n3", after);   // orphaned keys must move
            else if (before != after) moved++;                  // keys owned by surviving nodes should stay
        }
        Assert.Equal(0, moved);   // consistent hashing: only the removed node's keys remap
    }

    [Fact]
    public void GetNodes_Returns_Distinct_Replicas()
    {
        var ring = new ShardRing(ThreeNodes());
        var replicas = ring.GetNodes("some-room", 2);
        Assert.Equal(2, replicas.Count);
        Assert.NotEqual(replicas[0].NodeId, replicas[1].NodeId);
    }

    [Fact]
    public void Empty_Ring_Returns_Null()
    {
        var ring = new ShardRing(Array.Empty<ShardNode>());
        Assert.Null(ring.GetNode("anything"));
        Assert.Empty(ring.GetNodes("anything", 3));
    }

    [Fact]
    public async Task Client_Locates_Key_Via_Directory()
    {
        var config = new Configuration { Host = "sharding", Port = 1 }.UseInMemory();
        var server = new TestServer(config);
        var directory = server.UseSharding(new ShardingOptions { Nodes = ThreeNodes(), SelfNodeId = "n1" });
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var sharding = client.UseSharding();
        await client.ConnectAsync();

        // The client's answer must match the server's local ring.
        var viaWire = await sharding.LocateAsync("room-ABC123");
        var local = directory.Locate("room-ABC123");
        Assert.Equal(local!.NodeId, viaWire.NodeId);
        Assert.Equal(local.Host, viaWire.Host);
        Assert.Equal(local.Port, viaWire.Port);

        var all = await sharding.ListNodesAsync();
        Assert.Equal(3, all.Count);

        // IsLocal agrees with Locate for the configured self id.
        Assert.Equal(local.NodeId == "n1", directory.IsLocal("room-ABC123"));

        // Membership update propagates to subsequent queries.
        directory.UpdateNodes(ThreeNodes().Where(n => n.NodeId != "n2"));
        var afterUpdate = await sharding.ListNodesAsync();
        Assert.Equal(2, afterUpdate.Count);

        client.Disconnect();
        await server.StopAsync();
    }
}
