// Chat client entry point.
//   dotnet run --project examples/Chat.Client -- [host] [port] [username]
// Defaults: 127.0.0.1 5000 user<pid>. Type lines to chat; "/quit" to exit.

using Chat.Client;
using SetNet.Config;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5000;
var username = args.Length > 2 ? args[2] : $"user{Environment.ProcessId}";

var config = new Configuration
{
    Host = host,
    Port = port,
    HeartbeatEnabled = true,
    AutoReconnect = true,
    MaxReconnectAttempts = 5,
    ReconnectDelayMs = 1000
};

var client = new ChatClient(config, username);

Console.WriteLine($"Connecting to {host}:{port} as '{username}'...");
await client.ConnectAsync();
Console.WriteLine("Type a message and press Enter. '/quit' to exit.");

while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (line.Length == 0) continue;
    await client.SendChatAsync(line);
}

client.Disconnect();
Console.WriteLine("Bye.");
