// Matchmaking server entry point.
//   dotnet run --project examples/Matchmaking/Matchmaking.Server -- [host] [port]
// Defaults: 127.0.0.1 5300. Queues players and auto-forms 2-player matches, dropping each into a room.
//
// The SAME MemoryRoomStore is passed to UseRooms and UseMatchmaking: a formed match creates a room in
// that store, which the matched clients then join through Rooms.

using Matchmaking.Server;
using SetNet.Config;
using SetNet.Matchmaking;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();          // load SetNet.Rooms so its channel service is discoverable
MatchmakingRuntime.Enable();    // load SetNet.Matchmaking so its channel service is discoverable

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5300;

var server = new DemoServer(new Configuration { Host = host, Port = port });

var store = new MemoryRoomStore();   // ONE store, shared by both layers
server.UseRooms(store);
server.UseMatchmaking(store, new MatchmakingOptions { MatchSize = 2, TickIntervalMs = 100 });

_ = server.StartAsync();

Console.WriteLine($"Matchmaking server on {host}:{port}. Queue -> 2-player match -> room. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
