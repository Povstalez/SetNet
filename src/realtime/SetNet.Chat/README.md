<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Chat

**Text chat for [SetNet](https://www.nuget.org/packages/SetNet).**

Almost every multiplayer game wants a chat box — a lobby chat, a team channel, a global feed. This package gives you a **channel-based, server-relayed** text chat by **composition**: clients join channels by name, send text to a channel, and receive everyone else's messages on the channels they've joined. The server relays and moderates; you don't write any of the plumbing.

It ships with light moderation built in:

- **length limiting** — messages longer than `MaxLength` are truncated,
- **profanity filtering** — a whole-word, case-insensitive censor that replaces matched words with asterisks,
- **auto-cleanup** — a peer is removed from all its channels when it disconnects.

Added alongside your regular messages — no base class, no subclassing.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Chat
```

## Setup

```csharp
ChatRuntime.Enable();      // once at startup, both ends, before creating client/server
```

## Server

```csharp
var chat = server.UseChat(new ChatOptions
{
    MaxLength      = 500,                                  // longer messages are truncated
    ProfanityWords = new[] { "badword", "anotherone" },   // whole-word, case-insensitive censor
});
```

That's it — the server relays each message to every peer on the same channel and drops disconnected peers from their channels automatically. `UseChat()` with no options uses the defaults (`MaxLength = 500`, no profanity list).

## Client

```csharp
var chat = client.UseChat();

chat.MessageReceived += (channel, senderPlayerId, text) =>
    AppendToChatBox(channel, senderPlayerId, text);

// join the channels you care about:
await chat.JoinAsync("global");
await chat.JoinAsync("team-red");

// send a message to a channel:
await chat.SendAsync("global", "gg wp");

// leave a channel when you're done:
await chat.LeaveAsync("team-red");
```

`senderPlayerId` is the sender's peer id as a compact string (`Guid.ToString("N")`). You only receive messages for channels you've joined.

## API

**Client — `ChatClient` (`client.UseChat()`):**

| Member | Purpose |
|---|---|
| `Task JoinAsync(string channel)` | join a channel |
| `Task LeaveAsync(string channel)` | leave a channel |
| `Task SendAsync(string channel, string text)` | send a message to a channel |
| `event Action<string,string,string> MessageReceived` | incoming message — `(channel, senderPlayerId, text)` |

**Server — `ChatServer` (`server.UseChat(options?)`):** installs the channel relay + moderation and auto-removes peers on disconnect.

**`ChatOptions`:**

| Option | Default | Purpose |
|---|---|---|
| `MaxLength` | `500` | messages longer than this are truncated |
| `ProfanityWords` | `null` | words to censor (whole-word, case-insensitive), replaced with asterisks |

## Notes

- **Server-relayed, not peer-to-peer.** The server is the hub: it moderates and fans a message out to the channel's members. Sanitizing happens server-side, so clients can't bypass it.
- **Profanity matching is case-insensitive substring** replacement; each match is swapped for the same number of asterisks. It's a simple filter, not a full moderation system — layer your own logic on top if you need more.
- **Channels are ad-hoc** — a channel exists as soon as someone joins it; there's no separate "create". Use any naming scheme you like (`"global"`, `"team-red"`, `$"room-{code}"`).
- **Reliable delivery.** Chat rides `DeliveryMethod.Reliable`, so messages are ordered and not dropped.
- **Node-local.** Channels are live peers on one server node. A multi-node deployment would coordinate through a shared bus.
- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Chat` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
