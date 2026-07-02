// Matchmaking client entry point.
//   dotnet run --project examples/Matchmaking/Matchmaking.Client -- [host] [port]
// Defaults: 127.0.0.1 5300.
//
// To see a match form: start the server, then run TWO clients. Each enters the "ranked" queue; the server
// pairs them, creates a room, and both clients auto-join it — printing the shared room code and its members.

using Matchmaking.Client;
using Matchmaking.Shared;
using SetNet.Config;
using SetNet.Matchmaking;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();
MatchmakingRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5300;

var client = new DemoClient(new Configuration { Host = host, Port = port });
var rooms = client.UseRooms();
var mm = client.UseMatchmaking();

// See other players arrive in the room once we've been matched and joined it.
rooms.PlayerJoined += id => Console.WriteLine($"* player {id} joined the room");

Console.WriteLine($"Connecting to {host}:{port}...");
await client.ConnectAsync();

Console.WriteLine($"Searching queue \"{MatchmakingDemo.Queue}\" — waiting for another player...");
var room = await mm.FindAndJoinAsync(new MatchRequest { Queue = MatchmakingDemo.Queue }, rooms);

Console.WriteLine($"Matched! Joined room {room.Code} with members: {string.Join(", ", room.Members)}");
Console.WriteLine("Press Enter to leave.");
Console.ReadLine();

client.Disconnect();
Console.WriteLine("Bye.");
