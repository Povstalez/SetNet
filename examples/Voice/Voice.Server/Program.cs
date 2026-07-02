// Voice relay server entry point.
//   dotnet run --project examples/Voice/Voice.Server -- [host] [port]
// Defaults: 127.0.0.1 5321. Fans each opaque voice frame out to the channel's other members.

using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Voice;
using Voice.Server;
using Voice.Shared;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
VoiceRuntime.Enable();   // load SetNet.Voice so its byte[] handlers are discoverable

var host = args.Length > 0 ? args[0] : VoiceProtocol.DefaultHost;
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : VoiceProtocol.DefaultPort;

var server = new VoiceRelayServer(new Configuration { Host = host, Port = port });
server.UseVoice();   // turn on the relay hub — the server never decodes audio, just forwards bytes
_ = server.StartAsync();

Console.WriteLine($"Voice relay server on {host}:{port}. Relaying channel frames. Press Enter to stop.");
Console.ReadLine();
await server.StopAsync();
Console.WriteLine("Server stopped.");
