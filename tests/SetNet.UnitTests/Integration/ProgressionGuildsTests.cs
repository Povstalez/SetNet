using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Guilds;
using SetNet.InMemory;
using SetNet.Inventory;
using SetNet.LoadBalancer;
using SetNet.Progression;
using SetNet.Quests;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for progression, quests, guilds, and the load balancer.</summary>
[Collection("integration")]
public class ProgressionGuildsTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    private static Func<BasePeer, string> OrderedKeys(BaseServer server)
    {
        var map = new ConcurrentDictionary<Guid, string>();
        var counter = 0;
        server.PeerConnected += peer => map[peer.CurrentPeerInfo.Id] = $"player{System.Threading.Interlocked.Increment(ref counter) - 1}";
        return peer => map.TryGetValue(peer.CurrentPeerInfo.Id, out var k) ? k : peer.CurrentPeerInfo.Id.ToString();
    }

    [Fact]
    public async Task Progression_Grants_Xp_And_Levels_Up()
    {
        var config = Config("prog");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var progression = server.UseProgression(options: new ProgressionOptions { PlayerKey = keys, XpForLevel = lvl => 100L * lvl, MaxLevel = 10 });
        var levels = new ConcurrentQueue<int>();
        progression.LeveledUp += (_, lvl) => levels.Enqueue(lvl);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var prog = client.UseProgression();
        ProgressionState? pushed = null;
        prog.Changed += s => pushed = s;
        await client.ConnectAsync();
        await Task.Delay(150);

        // Level 1 needs 100 to reach 2; level 2 needs 200 to reach 3. 350 XP → level 3 with 50 into it.
        var state = await progression.GrantXpAsync("player0", 350);
        Assert.Equal(3, state.Level);
        Assert.Equal(50, state.Xp);
        Assert.Equal(new[] { 2, 3 }, levels.ToArray());   // fired once per level crossed

        Assert.True(await Wait(() => pushed != null && pushed.Level == 3));
        var read = await prog.GetAsync();
        Assert.Equal(3, read.Level);
        Assert.Equal(300, read.XpToNext);   // level 3 → 4 needs 100*3

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Quests_Accept_Progress_And_Claim()
    {
        var config = Config("quest");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var quests = server.UseQuests(inventory).Define(new QuestDefinition("hunt",
            objectives: new[] { new QuestObjective("kill", 3) },
            rewards: new[] { new ItemStack("gold", 50) }));
        var completed = new ConcurrentQueue<string>();
        quests.QuestCompleted += (_, questId) => completed.Enqueue(questId);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var q = client.UseQuests();
        QuestView? last = null;
        q.Updated += v => last = v;
        await client.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        await q.AcceptAsync("hunt");
        await quests.ProgressAsync("player0", "kill", 2);
        Assert.True(await Wait(() => last != null && last.Objectives.Single().Current == 2 && !last.Completable));

        await quests.ProgressAsync("player0", "kill", 5);   // caps at 3 → completable
        Assert.True(await Wait(() => last != null && last.Completable));
        Assert.Equal(3, last!.Objectives.Single().Current);
        Assert.Contains("hunt", completed);

        await q.ClaimAsync("hunt");
        Assert.Equal(50, (await inventory.GetAsync("player0")).Single(s => s.ItemId == "gold").Count);

        // Can't claim twice.
        await Assert.ThrowsAsync<QuestException>(() => q.ClaimAsync("hunt"));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Guild_Create_Join_And_Bank()
    {
        var config = Config("guild");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseGuilds(inventory);
        _ = server.StartAsync();
        await Task.Delay(120);

        var leaderClient = new TestClient(config); var leaderGuild = leaderClient.UseGuilds();
        var joined = new ConcurrentQueue<string>();
        leaderGuild.MemberJoined += key => joined.Enqueue(key);
        await leaderClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        var memberClient = new TestClient(config); var memberGuild = memberClient.UseGuilds();
        await memberClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player1") != null));

        var guildId = await leaderGuild.CreateAsync("Testers");
        await memberGuild.JoinAsync(guildId);
        Assert.True(await Wait(() => joined.Contains("player1")));   // leader saw the join

        var members = await leaderGuild.ListMembersAsync();
        Assert.Equal(2, members.Count);
        Assert.Equal(GuildRole.Leader, members.Single(m => m.PlayerKey == "player0").Role);

        // Bank: member deposits, leader withdraws; a plain member cannot withdraw.
        await inventory.GrantAsync("player1", "gold", 100);
        await memberGuild.BankDepositAsync("gold", 100);
        Assert.Empty(await inventory.GetAsync("player1"));
        Assert.Equal(100, (await memberGuild.BankListAsync()).Single(s => s.ItemId == "gold").Count);

        await Assert.ThrowsAsync<GuildException>(() => memberGuild.BankWithdrawAsync("gold", 50));   // member can't withdraw
        await leaderGuild.BankWithdrawAsync("gold", 40);
        Assert.Equal(40, (await inventory.GetAsync("player0")).Single(s => s.ItemId == "gold").Count);
        Assert.Equal(60, (await leaderGuild.BankListAsync()).Single(s => s.ItemId == "gold").Count);

        leaderClient.Disconnect(); memberClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task LoadBalancer_Picks_Least_Loaded()
    {
        var config = Config("lb");
        var server = new TestServer(config);
        var lb = server.UseLoadBalancer();
        lb.UpdateNode(new LbNode("a", "host-a", 5000, load: 40, capacity: 100));
        lb.UpdateNode(new LbNode("b", "host-b", 5000, load: 10, capacity: 100));   // emptiest
        lb.UpdateNode(new LbNode("c", "host-c", 5000, load: 100, capacity: 100));  // full
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var picker = client.UseLoadBalancer();
        await client.ConnectAsync();

        var node = await picker.PickAsync();
        Assert.Equal("b", node.NodeId);   // lowest load ratio with capacity

        // Load 'b' up past 'a'; now 'a' is emptiest.
        lb.ReportLoad("b", 60);
        Assert.Equal("a", (await picker.PickAsync()).NodeId);

        // Fill everything → no node available.
        lb.ReportLoad("a", 100); lb.ReportLoad("b", 100);
        await Assert.ThrowsAsync<LoadBalancerException>(() => picker.PickAsync());

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> Wait(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}
