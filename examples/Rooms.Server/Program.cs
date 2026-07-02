// Rooms lobby server.
//   dotnet run --project examples/Rooms.Server -- [host] [port]
// Defaults: 127.0.0.1 5000. Press Enter to stop.
//
// The server does almost nothing itself: `server.UseRooms()` wires up create/join-by-code, broadcast
// relay, join/leave events and auto-leave on disconnect. Clients drive it all.

using Rooms.Server;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());   // core bundles no serializer
RoomsRuntime.Enable();                                  // load Rooms before handler discovery

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;

var server = new LobbyServer(new Configuration { Host = host, Port = port, HeartbeatEnabled = true });
server.UseRooms();                                      // <-- the whole feature
_ = server.StartAsync();

Console.WriteLine($"Rooms lobby server listening on {host}:{port}. Press Enter to stop.");
Console.ReadLine();

await server.StopAsync();
Console.WriteLine("Server stopped.");
