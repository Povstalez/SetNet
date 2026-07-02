// RPC client entry point.
//   dotnet run --project examples/Rpc.Client -- [host] [port]
// Calls GetTime and Add on the server and prints the results.
// (client.CallAsync IS client.RequestAsync on the reserved Channels.Rpc channel — one request/reply mechanism.)

using Rpc.Client;
using Rpc.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rpc;

SetNetSerializer.Use(new MessagePackNetSerializer());
RpcRuntime.Enable();

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5100;

var client = new DemoClient(new Configuration { Host = host, Port = port });
Console.WriteLine($"Connecting to {host}:{port}...");
await client.ConnectAsync();

var time = await client.CallAsync<TimeRequest, TimeReply>(RpcMethods.GetTime, new TimeRequest());
Console.WriteLine($"server UTC time: {time.UtcNow}");

var sum = await client.CallAsync<AddRequest, AddReply>(RpcMethods.Add, new AddRequest { A = 2, B = 40 });
Console.WriteLine($"2 + 40 = {sum.Sum}");

client.Disconnect();
Console.WriteLine("Done.");
