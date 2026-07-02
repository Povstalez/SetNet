// Rooms lobby client — join players into a shared room by code and chat within it.
//
//   Player 1 (host):  dotnet run --project examples/Rooms.Client -- 127.0.0.1 5000 create
//                     -> prints a room code, e.g. "K7Q2MZ"
//   Player 2:         dotnet run --project examples/Rooms.Client -- 127.0.0.1 5000 K7Q2MZ
//
// Then type lines and press Enter — they broadcast to everyone else in the room. Type /quit to exit.
//
// Shows: SetNet.Rooms create/join-by-code + the TYPED broadcast API
//   rooms.BroadcastAsync<T>(messageType, msg)  ->  rooms.On<T>(messageType, (from, msg) => ...)

using Rooms.Client;
using Rooms.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;
var joinArg = args.Length > 2 ? args[2] : "create";

// Heartbeat must be enabled on BOTH ends: the client pings, the server watches for those pings and
// drops a peer that goes silent. (Without this an idle client is dropped after the heartbeat timeout.)
var client = new LobbyClient(new Configuration { Host = host, Port = port, HeartbeatEnabled = true });
var rooms = client.UseRooms();

rooms.PlayerJoined += id => Console.WriteLine($"* {Short(id)} joined the room");
rooms.PlayerLeft   += id => Console.WriteLine($"* {Short(id)} left the room");

// Typed handler: the ChatLine message-type is deserialized to RoomChatLine for us — no raw bytes.
rooms.On<RoomChatLine>((ushort)RoomMsg.ChatLine, (from, line) => Console.WriteLine($"[{Short(from)}] {line.Text}"));

await client.ConnectAsync();

if (string.Equals(joinArg, "create", StringComparison.OrdinalIgnoreCase))
{
    var info = await rooms.CreateAsync();
    Console.WriteLine($"Created room. Share this code: {info.Code}");
}
else
{
    var info = await rooms.JoinAsync(joinArg);
    Console.WriteLine($"Joined room {info.Code} ({info.Members.Count} players).");
}

Console.WriteLine("Type a message and press Enter (/quit to exit):");
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (line == "/quit") break;
    await rooms.BroadcastAsync((ushort)RoomMsg.ChatLine, new RoomChatLine { Text = line });   // typed send
}

try { await rooms.LeaveAsync(); } catch { /* connection may already be down */ }
try { client.Disconnect(); } catch { /* ignore */ }

static string Short(string playerId) => playerId.Length >= 6 ? playerId[..6] : playerId;
