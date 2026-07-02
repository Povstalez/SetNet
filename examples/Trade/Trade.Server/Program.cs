// Trade server entry point.
//   dotnet run --project examples/Trade/Trade.Server -- [host] [port]
// Defaults: 127.0.0.1 5310. Inventory (authoritative items) + Trade (escrow two-phase swap).
// On connect it grants each player a starter kit and prints their inventory key — copy that key
// into the OTHER client's `propose <key>` command to start a trade.

using SetNet.Config;
using SetNet.Inventory;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Trade;
using Trade.Server;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
// Load the modules BEFORE constructing the server so their channel services are discoverable.
InventoryRuntime.Enable();
TradeRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5310;

var server = new DemoServer(new Configuration { Host = host, Port = port });
_ = server.StartAsync();

Console.WriteLine($"Trade server on {host}:{port}. Inventory + Trade. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
