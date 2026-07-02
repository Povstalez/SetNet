// RPC server entry point.
//   dotnet run --project examples/Rpc.Server -- [host] [port]
// Defaults: 127.0.0.1 5100. Exposes GetTime + Add via [RpcMethod] handlers.

using Rpc.Server;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rpc;

SetNetSerializer.Use(new MessagePackNetSerializer());
RpcRuntime.Enable();   // load SetNet.Rpc so its channel service + [RpcMethod] handlers are discoverable

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5100;

var server = new DemoServer(new Configuration { Host = host, Port = port });
_ = server.StartAsync();

Console.WriteLine($"RPC server on {host}:{port}. Methods: GetTime, Add. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
