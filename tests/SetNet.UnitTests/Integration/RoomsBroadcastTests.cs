using System;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Protocol;
using SetNet.Rooms;
using Xunit;

namespace SetNet.UnitTests.Integration;

internal static class RoomBcastChannel { public const ushort Id = 701; }
internal enum RoomBcastOp : ushort { Shout = 1 }
internal enum RoomBcastEvt : ushort { Shouted = 10 }

/// <summary>Server-side channel that fans a "shout" out to the other members of the shouter's room via the built-in helper.</summary>
[ProtocolChannel(RoomBcastChannel.Id)]
public sealed class RoomBcastService
{
    [Op((ushort)RoomBcastOp.Shout)]
    public Task Shout(BasePeer peer, NoteReq req)
        => peer.CurrentPeerInfo.Server!.BroadcastToRoomOfAsync(peer, RoomBcastChannel.Id, (ushort)RoomBcastEvt.Shouted, req);
}

/// <summary>Verifies server-side room membership queries and room-scoped broadcast (to others, sender excluded).</summary>
[Collection("integration")]
public class RoomsBroadcastTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task Server_Broadcasts_To_Room_Members_Except_Sender()
    {
        var server = new TestServer(Config(5941));
        server.UseRooms();
        _ = server.StartAsync();
        await Task.Delay(200);

        var a = new TestClient(Config(5941));
        var roomsA = a.UseRooms();
        await a.ConnectAsync();
        var room = await roomsA.CreateAsync();

        var b = new TestClient(Config(5941));
        var roomsB = b.UseRooms();
        await b.ConnectAsync();
        await roomsB.JoinAsync(room.Code);

        // Server-side membership query — no app-maintained map needed.
        Assert.Equal(2, server.MembersOfRoom(room.Code).Count);

        // A shouts → the server broadcasts to the room's OTHER members only (B), excluding the sender (A).
        // One process-wide subscription counts frames as they arrive: sender-exclusion means exactly one frame
        // reaches a socket (B's); had the server not excluded A, both sockets would receive it (two frames).
        // (A and B are co-located in this test, so we can't distinguish by which handler fires — hence a frame count.)
        var frames = 0;
        a.On<NoteReq>(RoomBcastChannel.Id, (ushort)RoomBcastEvt.Shouted, _ => System.Threading.Interlocked.Increment(ref frames));

        await a.PostAsync(RoomBcastChannel.Id, (ushort)RoomBcastOp.Shout, new NoteReq { Text = "hello-room" });

        Assert.True(await WaitUntil(() => frames >= 1));
        await Task.Delay(150);
        Assert.Equal(1, frames);   // exactly one frame on the wire → the sender was excluded

        // After B leaves, the room has one member (the index stays consistent).
        await roomsB.LeaveAsync();
        Assert.True(await WaitUntil(() => server.MembersOfRoom(room.Code).Count == 1));

        a.Disconnect();
        b.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (start.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
