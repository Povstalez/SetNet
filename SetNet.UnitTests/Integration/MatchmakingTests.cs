using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Matchmaking;
using SetNet.Rooms;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for SetNet.Matchmaking (on top of Rooms), over the fast in-memory transport.</summary>
[Collection("integration")]
public class MatchmakingTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Two_Players_Get_Matched_Into_The_Same_Room()
    {
        var store = new MemoryRoomStore();                 // shared between Rooms and Matchmaking
        var server = new TestServer(Config("mm-pair"));
        server.UseRooms(store);
        server.UseMatchmaking(store, new MatchmakingOptions { MatchSize = 2, TickIntervalMs = 100 });
        _ = server.StartAsync();
        await Task.Delay(100);

        var a = new TestClient(Config("mm-pair"));
        var roomsA = a.UseRooms();
        var mmA = a.UseMatchmaking();
        await a.ConnectAsync();

        var b = new TestClient(Config("mm-pair"));
        var roomsB = b.UseRooms();
        var mmB = b.UseMatchmaking();
        await b.ConnectAsync();

        var findA = mmA.FindMatchAsync(new MatchRequest { Queue = "ranked" });
        var findB = mmB.FindMatchAsync(new MatchRequest { Queue = "ranked" });
        var results = await Task.WhenAll(findA, findB);

        Assert.NotEmpty(results[0].RoomCode);
        Assert.Equal(results[0].RoomCode, results[1].RoomCode);   // same room
        Assert.Equal(2, results[0].Players.Count);
        Assert.Contains(results[1].OwnPlayerId, results[0].Players);  // each sees the other

        // The matched room actually exists and both can join it.
        var ra = await roomsA.JoinAsync(results[0].RoomCode);
        var rb = await roomsB.JoinAsync(results[1].RoomCode);
        Assert.Equal(ra.Code, rb.Code);

        a.Disconnect();
        b.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Different_Queues_Do_Not_Match()
    {
        var store = new MemoryRoomStore();
        var server = new TestServer(Config("mm-queues"));
        server.UseRooms(store);
        server.UseMatchmaking(store, new MatchmakingOptions { MatchSize = 2, TickIntervalMs = 100 });
        _ = server.StartAsync();
        await Task.Delay(100);

        var a = new TestClient(Config("mm-queues"));
        a.UseRooms(); var mmA = a.UseMatchmaking();
        await a.ConnectAsync();

        var b = new TestClient(Config("mm-queues"));
        b.UseRooms(); var mmB = b.UseMatchmaking();
        await b.ConnectAsync();

        var findA = mmA.FindMatchAsync(new MatchRequest { Queue = "solo" });
        var findB = mmB.FindMatchAsync(new MatchRequest { Queue = "duo" });

        // Different queues can't fill a 2-player match → neither completes within the window.
        var completed = await Task.WhenAny(Task.WhenAll(findA, findB), Task.Delay(800));
        Assert.NotEqual(TaskStatus.RanToCompletion, findA.Status);
        Assert.NotEqual(TaskStatus.RanToCompletion, findB.Status);

        await mmA.CancelAsync();
        await mmB.CancelAsync();
        a.Disconnect();
        b.Disconnect();
        await server.StopAsync();
    }
}
