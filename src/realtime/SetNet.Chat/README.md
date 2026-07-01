# SetNet.Chat

**Text chat for [SetNet](https://www.nuget.org/packages/SetNet).**

Channel-based chat: join/leave channels by name, server-relays messages to a channel's members, with a length limit and a simple whole-word profanity filter. Works alongside your regular messages.

```csharp
ChatRuntime.Enable();   // startup, both ends

// server:
server.UseChat(new ChatOptions { MaxLength = 300, ProfanityWords = new[] { "badword" } });

// client:
var chat = client.UseChat();
chat.MessageReceived += (channel, sender, text) => Print($"[{channel}] {sender}: {text}");
await chat.JoinAsync("global");
await chat.SendAsync("global", "gg wp");
```

Members are auto-removed from their channels on disconnect.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
