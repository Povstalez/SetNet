using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Rooms;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for SetNet.Rooms: create/join by code, broadcast, join/leave events, auto-leave on disconnect.</summary>
[Collection("integration")]
public class RoomsTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task Create_Join_Broadcast_And_JoinEvent()
    {
        var server = new TestServer(Config(5891));
        server.UseRooms();
        _ = server.StartAsync();
        await Task.Delay(200);

        var a = new TestClient(Config(5891));
        var roomsA = a.UseRooms();
        await a.ConnectAsync();
        var room = await roomsA.CreateAsync(new RoomOptions { MaxPlayers = 4 });
        Assert.NotEmpty(room.Code);

        string? joinedId = null;
        roomsA.PlayerJoined += id => joinedId = id;

        var b = new TestClient(Config(5891));
        var roomsB = b.UseRooms();
        await b.ConnectAsync();
        var bRoom = await roomsB.JoinAsync(room.Code);

        Assert.Equal(room.Code, bRoom.Code);
        Assert.Contains(room.OwnPlayerId, bRoom.Members);          // A is visible to B
        Assert.True(await WaitUntil(() => joinedId == bRoom.OwnPlayerId));  // A saw B join

        byte[]? gotPayload = null;
        string? gotFrom = null;
        roomsA.MessageReceived += (from, payload) => { gotFrom = from; gotPayload = payload; };
        await roomsB.BroadcastAsync(new byte[] { 1, 2, 3 });

        Assert.True(await WaitUntil(() => gotPayload != null));
        Assert.Equal(bRoom.OwnPlayerId, gotFrom);
        Assert.Equal(new byte[] { 1, 2, 3 }, gotPayload);

        a.Disconnect();
        b.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Disconnect_Auto_Leaves_And_Notifies()
    {
        var server = new TestServer(Config(5892));
        server.UseRooms();
        _ = server.StartAsync();
        await Task.Delay(200);

        var a = new TestClient(Config(5892));
        var roomsA = a.UseRooms();
        await a.ConnectAsync();
        var room = await roomsA.CreateAsync();

        string? leftId = null;
        roomsA.PlayerLeft += id => leftId = id;

        var b = new TestClient(Config(5892));
        var roomsB = b.UseRooms();
        await b.ConnectAsync();
        var bRoom = await roomsB.JoinAsync(room.Code);

        b.Disconnect();   // dropping should auto-remove B from the room and notify A

        Assert.True(await WaitUntil(() => leftId == bRoom.OwnPlayerId));

        a.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Join_Nonexistent_Room_Throws()
    {
        var server = new TestServer(Config(5893));
        server.UseRooms();
        _ = server.StartAsync();
        await Task.Delay(200);

        var a = new TestClient(Config(5893));
        var rooms = a.UseRooms();
        await a.ConnectAsync();

        await Assert.ThrowsAsync<RoomException>(() => rooms.JoinAsync("ZZZZZZ"));

        a.Disconnect();
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
