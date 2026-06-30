# SetNet 🌐

**A lightweight, high-throughput .NET networking library for client–server games and real-time apps — over TCP, UDP, or both at once.**

![.NET Standard 2.1](https://img.shields.io/badge/.NET%20Standard-2.1-512BD4)
![Serialization](https://img.shields.io/badge/serialization-MessagePack-blue)
![Transports](https://img.shields.io/badge/transport-TCP%20%7C%20UDP%20%7C%20Both-success)
![License](https://img.shields.io/badge/license-MIT-green)

SetNet gives you a persistent, message-oriented connection with automatic handler registration, a pluggable transport (reliable TCP, raw/reliable UDP, or both together), per-message delivery selection, and production-grade hardening — so you can focus on your game/app logic instead of sockets.

```csharp
// per-message channel selection — reliable for events, unreliable for movement
await SendAsync(MsgType.Chat,     chat,     DeliveryMethod.Reliable);
await SendAsync(MsgType.Position, position, DeliveryMethod.Unreliable);
```

## Why SetNet

- 🚦 **TCP / UDP / Both** — one API, choose per `Configuration.TransportType`; pick the channel per message via `DeliveryMethod`.
- 🛡️ **Reliable UDP, optional** — sequence / ACK / retransmit / ordered delivery with a bounded receive window and back-pressure; multiple independent channels (`UdpReliableChannels`) so a loss on one stream never head-of-line-blocks another.
- 🤝 **Emulated UDP connections** — handshake + heartbeat give UDP the same `OnConnected`/`OnDisconnected`/peer lifecycle as TCP. **Both mode** binds a TCP lifeline and a UDP channel to one logical peer, with graceful TCP-only fallback.
- 🔄 **Lifecycle done right** — intentional vs unexpected disconnects, auto-reconnect hooks, heartbeat liveness; `OnDisconnected` fires exactly once.
- ⚡ **Fast** — ~**1.8M msgs/sec** on one connection with send batching, ~4 KB per endpoint; allocation-light hot paths.
- 🔒 **Production-hardened** — TLS over TCP, connection/UDP-peer caps, per-IP rate limiting, frame-size cap, back-pressure, bounded inbound queues (OOM protection), a resilient accept loop, and live `NetworkMetrics`.
- 🧩 **Auto handler registration** — mark a class `[MessageHandler(type)]`; reflection wires it up. MessagePack serialization.

## Install

Requires **.NET Standard 2.1** (consumable from .NET Core 3.0+/.NET 5–8, Unity, Mono, MAUI — not .NET Framework).

```bash
# project reference (NuGet package coming)
dotnet add reference path/to/SetNet/SetNet.csproj
```

## Quick start

**1. Define messages** (MessagePack DTOs):

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

**4. Handle messages** (auto-discovered):

```csharp
[MessageHandler((ushort)MsgType.Chat)]
public class ChatHandler : IServerMessageHandler
{
    public Task HandleAsync(BasePeer peer, byte[] data)
    {
        var msg = MessagePackSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

A full runnable chat (separate server + client processes) is in [`examples/`](examples).

## Transport selection

Set `Configuration.TransportType` (default `Tcp`, so existing TCP code is unchanged):

| TransportType | DeliveryMethod | Carried over |
|---|---|---|
| `Tcp`  | any | TCP |
| `Udp`  | Reliable | UDP reliability layer (needs `UdpReliabilityEnabled`) |
| `Udp`  | Unreliable | raw UDP datagram |
| `Both` | Reliable | TCP |
| `Both` | Unreliable | UDP (falls back to TCP until the UDP channel attaches) |

```csharp
var config = new Configuration
{
    Host = "127.0.0.1", Port = 5000,
    TransportType = TransportType.Both,
    UdpReliabilityEnabled = true,
    UdpReliableChannels = 2,          // independent ordered streams
    DefaultDelivery = DeliveryMethod.Reliable,
};
```

## Lifecycle at a glance

`BaseClient` distinguishes intentional from unexpected disconnects; `OnDisconnected` fires exactly once.

| Event | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| `Disconnect()` (intentional) | ❌ | ❌ | ✅ | ❌ |
| Network error / server crash | ✅ | ✅ | ✅ (if reconnect fails) | ✅ (if enabled) |
| Graceful server close | ❌ | ❌ | ✅ | ❌ |

Enable: `AutoReconnect = true`, `HeartbeatEnabled = true` (both off by default).

## Production hardening

```csharp
var config = new Configuration
{
    Host = "0.0.0.0", Port = 5000,
    UseSsl = true, ServerCertificate = cert,        // TLS over TCP (UDP is not encrypted)
    MaxConnectionsLimit = 5000,
    MaxConnectionsPerIpPerSecond = 20,              // per-IP rate limit
    MaxInFlightMessages = 256,                      // handler back-pressure
    MaxInboundQueue = 16384,                        // per-connection inbound cap (OOM protection)
    HeartbeatEnabled = true,
};
```

**Authentication is intentionally left to your application** — validate inside `OnNewClient`/handlers. UDP has no per-packet encryption; route sensitive data over TLS-over-TCP (or Both with reliable delivery).

## Performance

In-process benchmark (`dotnet run -c Release --project SetNet.Tests -- bench`, ServerGC):

| Mode | Throughput (1 connection) | Optimized for |
|---|---|---|
| Batched (`SendBatching = true`) | **~1.8M msgs/sec** | throughput |
| Default (`TcpNoDelay = true`) | ~240k msgs/sec | latency |

~4 KB per endpoint; 2,000 connections established in ~110 ms. The default favors latency (every small message sent immediately); enable `SendBatching` for high message rates. Full model, scaling limits and roadmap: [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Documentation

- 📖 **[Детальний посібник користувача (docs/GUIDE.md)](docs/GUIDE.md)** — full usage manual (Ukrainian): handlers, transports, reliable channels, reconnect, batching, hardening, the complete `Configuration` reference, and a production checklist.
- ⚙️ **[docs/PERFORMANCE.md](docs/PERFORMANCE.md)** — performance model, scaling limits, structural roadmap.
- 🏗️ **[CLAUDE.md](CLAUDE.md)** / **[AGENTS.md](AGENTS.md)** — architecture overview for contributors and coding agents.

## Build & test

```bash
dotnet build                                              # build (library targets netstandard2.1)
dotnet test SetNet.UnitTests/SetNet.UnitTests.csproj      # 72 unit + integration tests
dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>   # in-process transport scenarios
dotnet run --project SetNet.Tests -- bench                # throughput / connection benchmark

# chat example (two terminals)
dotnet run --project examples/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 alice
```

## Project structure

```
SetNet/             core library (transport abstraction, reliability, hardening)
SetNet.Tests/       in-process scenario harness + benchmark
SetNet.UnitTests/   xUnit unit + integration tests
examples/           runnable chat (Chat.Shared / Chat.Server / Chat.Client)
docs/               GUIDE.md, PERFORMANCE.md
```

## Status

SetNet has been through extensive adversarial auditing (multi-round correctness convergence + a performance pass) with a full unit/integration suite and in-process scenarios. It is well-suited as the **network layer for .NET ↔ .NET real-time systems** (multiplayer games, chat, collaborative apps).

It is **not** a general-purpose RPC/HTTP framework: there is no request/response correlation, no DI/hosting integration, and no WebSocket/browser transport. Before production, implement authentication, set the hardening config, and run a soak/load test under realistic traffic.

## License

MIT — see [LICENSE](LICENSE).
