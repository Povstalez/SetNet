// Presence / pub-sub client. Run several against one server, each on a topic:
//   dotnet run --project examples/Presence/Presence.Client -- [topic]
// Subscribes to <topic> (default "general"), then each typed line is published to it. Every subscriber of that
// topic sees it. '/quit' exits. Built purely on SetNet.Protocol: PostAsync (subscribe/publish) + On<T> (receive).

using Presence.Client;
using Presence.Shared;
using SetNet.Config;
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Protocol;

SetNetSerializer.Use(new MessagePackNetSerializer());

var topic = args.Length > 0 ? args[0] : "general";

var client = new PresenceClient(new Configuration { Host = "127.0.0.1", Port = 5330 });

// Receive messages for topics we're subscribed to.
client.On<TopicMessage>(PresenceChannel.Id, (ushort)PresenceEvt.Message,
    m => Console.WriteLine($"[{m.Topic}] {m.From}: {m.Text}"));

await client.ConnectAsync();
await client.PostAsync(PresenceChannel.Id, (ushort)PresenceOp.Subscribe, new TopicRef { Topic = topic });
Console.WriteLine($"subscribed to '{topic}'. Type to publish; '/quit' to exit.");

while (true)
{
    var line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (line.Length == 0) continue;
    await client.PostAsync(PresenceChannel.Id, (ushort)PresenceOp.Publish, new PublishReq { Topic = topic, Text = line });
}

client.Disconnect();
Console.WriteLine("Bye.");
