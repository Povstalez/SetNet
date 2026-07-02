// File-transfer server entry point.
//   dotnet run --project examples/FileTransfer/FileTransfer.Server -- [host] [port]
// Defaults: 127.0.0.1 5320. Receives client uploads via SetNet.Streams and prints their size.

using System.Text;
using FileTransfer.Server;
using FileTransfer.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Streams;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
StreamsRuntime.Enable();   // load SetNet.Streams so its byte[] handlers are discoverable

var host = args.Length > 0 ? args[0] : FileTransferProtocol.DefaultHost;
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : FileTransferProtocol.DefaultPort;

var server = new FileServer(new Configuration { Host = host, Port = port });

// Attach the streaming hub. Small offers are auto-accepted into an in-memory sink (StreamsOptions.AutoAccept),
// so we just wait for the completed transfer and report what arrived.
var streams = server.UseStreams();
streams.Received += (peer, s) =>
{
    var bytes = ((MemoryStreamSink)s.Sink).ToArray();
    var preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 40));
    Console.WriteLine($"[server] received '{s.Name}' ({s.Length} bytes) from {peer.CurrentPeerInfo.Id}");
    Console.WriteLine($"[server]   preview: {preview}...");
};

_ = server.StartAsync();

Console.WriteLine($"File-transfer server on {host}:{port}. Waiting for uploads. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
