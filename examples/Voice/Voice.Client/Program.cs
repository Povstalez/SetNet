// Voice relay client entry point.
//   dotnet run --project examples/Voice/Voice.Client -- [host] [port]
// Defaults: 127.0.0.1 5321.
//
// Run the server plus TWO of these clients. Each typed line is turned into a fake "audio frame"
// (its UTF-8 bytes) and relayed to the other client, which prints it as an incoming frame — a
// loopback of opaque bytes. Real audio would be Opus/PCM produced by a capture+encode loop.
// Type "/quit" to exit.

using System.Text;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Voice;
using Voice.Client;
using Voice.Shared;

// The core library bundles no serializer; register the MessagePack adapter once at startup.
SetNetSerializer.Use(new MessagePackNetSerializer());
VoiceRuntime.Enable();

var host = args.Length > 0 ? args[0] : VoiceProtocol.DefaultHost;
var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : VoiceProtocol.DefaultPort;

var client = new VoiceRelayClient(new Configuration { Host = host, Port = port });
Console.WriteLine($"Connecting to {host}:{port}...");
await client.ConnectAsync();

var voice = client.UseVoice();

// Each relayed frame carries a stable per-speaker id assigned by the server.
voice.FrameReceived += (speakerId, ch, audio) =>
    Console.WriteLine($"[voice] {audio.Length} bytes from speaker {speakerId} on ch {ch}: {Encoding.UTF8.GetString(audio)}");

await voice.JoinChannel(VoiceProtocol.Channel);

Console.WriteLine($"Joined channel {VoiceProtocol.Channel}. Type a line to send it as an audio frame. '/quit' to exit.");
while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (line.Length == 0) continue;

    // Treat the line's bytes as one opaque audio frame and relay it to the channel.
    await voice.SendFrame(VoiceProtocol.Channel, Encoding.UTF8.GetBytes(line));
}

client.Disconnect();
Console.WriteLine("Bye.");
