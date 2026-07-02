// File-transfer client entry point.
//   dotnet run --project examples/FileTransfer/FileTransfer.Client -- [host] [port]
// Defaults: 127.0.0.1 5320. Uploads a payload to the server with progress, then disconnects.
// Non-interactive: a single clean end-to-end transfer via SetNet.Streams.

using System.Text;
using FileTransfer.Client;
using FileTransfer.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Streams;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
StreamsRuntime.Enable();

var host = args.Length > 0 ? args[0] : FileTransferProtocol.DefaultHost;
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : FileTransferProtocol.DefaultPort;

var client = new FileClient(new Configuration { Host = host, Port = port });
Console.WriteLine($"Connecting to {host}:{port}...");
await client.ConnectAsync();

var streams = client.UseStreams();

// Build a small text payload and upload it. Progress is reported as the chunks flow.
var payload = Encoding.UTF8.GetBytes("Hello from SetNet.Streams! " + new string('x', 5000));
var progress = new Progress<double>(p => Console.WriteLine($"progress {p:P0}"));

Console.WriteLine($"Uploading 'hello.txt' ({payload.Length} bytes)...");
await streams.SendAsync("hello.txt", new MemoryStream(payload), progress);

Console.WriteLine("Done. Transfer confirmed by the server.");
client.Disconnect();
