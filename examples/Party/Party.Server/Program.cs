// Party server entry point.
//   dotnet run --project examples/Party/Party.Server -- [host] [port]
// Defaults: 127.0.0.1 5301. Hosts parties (create/join by code, ready-up, leader promotion) — node-local, no store.

using Party.Server;
using Party.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Party;

SetNetSerializer.Use(new MessagePackNetSerializer());
PartyRuntime.Enable();   // load SetNet.Party so its channel service is discoverable

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : PartyDemo.DefaultPort;

var server = new DemoServer(new Configuration { Host = host, Port = port });
server.UseParties();

_ = server.StartAsync();

Console.WriteLine($"Party server on {host}:{port}. Create/join parties by code. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
