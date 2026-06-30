# SetNet

**A lightweight, high-throughput .NET networking library for client–server games and real-time apps — over TCP, UDP, or both at once.**

SetNet gives you a persistent, message-oriented connection with automatic handler registration, a pluggable transport (reliable TCP, raw/reliable UDP, or both together), per-message delivery selection, strongly-typed handlers, and production-grade hardening — so you can focus on your game/app logic instead of sockets.

## Why SetNet

- 🚦 **TCP / UDP / Both** — one API; choose per `Configuration.TransportType`, pick the channel per message via `DeliveryMethod`.
- 🛡️ **Reliable UDP (optional)** — sequence / ACK / retransmit / ordered delivery with a bounded receive window and back-pressure; multiple independent channels so a loss on one stream never head-of-line-blocks another.
- 🤝 **Emulated UDP connections** — handshake + heartbeat give UDP the same `OnConnected`/`OnDisconnected`/peer lifecycle as TCP. **Both mode** binds a TCP lifeline and a UDP channel to one logical peer, with graceful TCP-only fallback.
- 🔄 **Lifecycle done right** — intentional vs unexpected disconnects, auto-reconnect hooks, heartbeat liveness; `OnDisconnected` fires exactly once.
- 🧩 **Strongly-typed handlers** — `IServerMessageHandler<T>` / `IClientMessageHandler<T>` receive the deserialized message; the library (de)serializes for you. Auto-discovered via `[MessageHandler(type)]`.
- 🔀 **Raw relay escape hatch** — override `OnRawFrame(type, data)` + `SendRawAsync` to forward bytes **without (de)serializing** (relay/proxy), while normal handlers stay typed.
- 📦 **Pluggable serialization** — the core bundles **no** serializer. Add [**SetNet.MessagePack**](https://www.nuget.org/packages/SetNet.MessagePack) (hardened MessagePack) or supply your own `ISerializer` (JSON, Protobuf, …).
- 🔒 **Production-hardened** — TLS over TCP, connection/UDP-peer caps, per-IP rate limiting, frame-size cap, back-pressure, bounded inbound queues (OOM protection), a resilient accept loop, and live `NetworkMetrics`.
- ⚡ **Fast** — ~1.6M msgs/sec on one connection with send batching; allocation-light hot paths.

## Install

```bash
dotnet add package SetNet
# the core bundles no serializer — add one (or supply your own ISerializer):
dotnet add package SetNet.MessagePack
```

Register the serializer once at startup, before connecting:

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Use(new MessagePackNetSerializer());
```

## Quick start

**1. Define a message** (MessagePack DTO):

```csharp
public enum MsgType : ushort { Chat = 1 }

[MessagePackObject]
public class ChatMessage { [Key(0)] public string Text { get; set; } = ""; }
```

**2. Server:**

```csharp
using SetNet.Core;
using SetNet.Config;

public class ChatPeer : BasePeer
{
    public ChatPeer(PeerInfo info) : base(info) { }
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
}

public class ChatServer : BaseServer
{
    public ChatServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo info) => new ChatPeer(info);
}

await new ChatServer(new Configuration { Host = "0.0.0.0", Port = 5000 }).StartAsync();
```

**3. Client:**

```csharp
public class ChatClient : BaseClient
{
    public ChatClient(Configuration config) : base(config) { }
    protected override void OnConnected()    => Console.WriteLine("connected");
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
    public Task SayAsync(string text) => SendAsync((ushort)MsgType.Chat, new ChatMessage { Text = text });
}

var client = new ChatClient(new Configuration { Host = "127.0.0.1", Port = 5000 });
await client.ConnectAsync();
await client.SayAsync("hello");
```

**4. Handle messages** (auto-discovered, strongly typed — the library deserializes for you):

```csharp
[MessageHandler((ushort)MsgType.Chat)]
public class ChatHandler : IServerMessageHandler<ChatMessage>
{
    public Task HandleAsync(BasePeer peer, ChatMessage msg)
    {
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

## Transport selection

Set `Configuration.TransportType` (default `Tcp`):

| TransportType | DeliveryMethod | Carried over |
|---|---|---|
| `Tcp`  | any | TCP |
| `Udp`  | Reliable | UDP reliability layer (needs `UdpReliabilityEnabled`) |
| `Udp`  | Unreliable | raw UDP datagram |
| `Both` | Reliable | TCP |
| `Both` | Unreliable | UDP (falls back to TCP until the UDP channel attaches) |

## Compatibility

- **.NET Standard 2.1** — consumable from .NET Core 3.0+/.NET 5–8, Unity (2021+), Mono, MAUI. **Not** .NET Framework.
- **Unity:** handlers run on background threads (marshal to the main thread before touching the Unity API); IL2CPP/AOT needs pre-generated MessagePack formatters (or an AOT-friendly serializer); **WebGL is not supported** (no threads/sockets).

## Notes

- **Authentication is left to your application** — validate inside `OnNewClient`/handlers.
- UDP datagrams have no per-packet encryption — route sensitive traffic over TLS-over-TCP (or Both with reliable delivery).
- Not a general-purpose RPC/HTTP framework: no request/response correlation, no DI/hosting integration, no WebSocket/browser transport.

## Documentation & source

- 📖 **[User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)** · **[Performance](https://github.com/Povstalez/SetNet/blob/master/docs/PERFORMANCE.en.md)**
- 🐙 **Repository:** https://github.com/Povstalez/SetNet

## License

MIT
