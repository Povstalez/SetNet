// Economy / world server entry point.
//   dotnet run --project examples/Economy.Server -- [host] [port]
// Defaults: 127.0.0.1 5200. Rooms + Inventory + authoritative item drops.

using Economy.Server;
using SetNet.Config;
using SetNet.Inventory;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();
InventoryRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5200;

var server = new GameServer(new Configuration { Host = host, Port = port });
_ = server.StartAsync();

Console.WriteLine($"World server on {host}:{port}. Rooms + Inventory + item drops. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
