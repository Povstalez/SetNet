// Chat server entry point.
//   dotnet run --project examples/Chat.Server -- [host] [port]
// Defaults: 127.0.0.1 5000. Press Enter to stop.

using Chat.Server;
using SetNet.Config;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;

var config = new Configuration
{
    Host = host,
    Port = port,
    HeartbeatEnabled = true // detect dead clients
};

var server = new ChatServer(config);
_ = server.StartAsync();

Console.WriteLine($"Chat server listening on {host}:{port}. Press Enter to stop.");
Console.ReadLine();

await server.StopAsync();
Console.WriteLine("Server stopped.");
