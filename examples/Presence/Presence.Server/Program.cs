// Presence / pub-sub server. Pure SetNet.Protocol — no companion module.
//   dotnet run --project examples/Presence/Presence.Server -- [host] [port]
// Defaults: 127.0.0.1 5330.

using Presence.Server;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Use(new MessagePackNetSerializer());   // the PresenceService is discovered automatically (no Runtime.Enable needed)

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5330;

var server = new PresenceServer(new Configuration { Host = host, Port = port });
_ = server.StartAsync();

Console.WriteLine($"Presence/pub-sub server on {host}:{port}. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
