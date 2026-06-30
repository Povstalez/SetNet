# SetNet 🌐

**A lightweight, high-throughput .NET networking library for client–server games and real-time apps — over TCP, UDP, or both at once.**

![.NET Standard 2.1](https://img.shields.io/badge/.NET%20Standard-2.1-512BD4)
![Serialization](https://img.shields.io/badge/serialization-pluggable-blue)
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
- 🧩 **Auto handler registration** — mark a class `[MessageHandler(type)]`; reflection wires it up.
- 📦 **Pluggable serialization** — the core bundles no serializer. Pick a format via `ISerializer`: drop in the `SetNet.MessagePack` package (hardened MessagePack), or supply your own JSON/Protobuf/custom adapter, and register it once with `SetNetSerializer.Default`.

## Install

Requires **.NET Standard 2.1** (consumable from .NET Core 3.0+/.NET 5–8, Unity, Mono, MAUI — not .NET Framework).

```bash
# project references (NuGet packages coming)
dotnet add reference path/to/SetNet/SetNet.csproj
# the core bundles no serializer — add one (or supply your own ISerializer):
dotnet add reference path/to/SetNet.MessagePack/SetNet.MessagePack.csproj
```

Then register the serializer once at startup, before connecting:

```csharp
SetNetSerializer.Default = new MessagePackNetSerializer();   // from SetNet.MessagePack
```

> **Unity:** works on desktop/mobile standalone (Unity 2021+, netstandard2.1). Two things to know: message handlers run on **background threads**, so marshal to the main thread before touching the Unity API (e.g. queue and drain in `Update()`); and on **IL2CPP/AOT** builds, MessagePack needs pre-generated formatters (or swap in an AOT-friendly serializer — see [Serialization](#serialization)). **WebGL is not supported** (no threads/sockets).

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

A full runnable chat (separate server + client processes) is in [`examples/`](examples).

## Serialization

The core library **bundles no serializer** — you choose the format behind the `ISerializer` seam and register it once at startup.

**MessagePack** (recommended) via the `SetNet.MessagePack` package — `MessagePackNetSerializer` is hardened with the `UntrustedData` security profile (deserialization-DoS protection):

```csharp
using SetNet.MessagePack;
SetNetSerializer.Default = new MessagePackNetSerializer();   // once, at startup
```

**Or your own format** (JSON, Protobuf, MemoryPack, …) — implement `ISerializer`:

```csharp
public sealed class JsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => System.Text.Json.JsonSerializer.Deserialize<T>(data)!;
}

SetNetSerializer.Default = new JsonSerializer();             // once, at startup
```

Handlers are **strongly typed** — they receive the deserialized message directly (`IServerMessageHandler<ChatMessage>` → `HandleAsync(peer, ChatMessage msg)`); the library serializes on send and deserializes on receive through this one `SetNetSerializer.Default`. Both ends of a connection must use the same serializer. (Until one is set, send/receive throws a clear "configure a serializer" error.)

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
| Batched (`SendBatching = true`) | **~1.6M msgs/sec** | throughput |
| Default (`TcpNoDelay = true`) | ~230k msgs/sec | latency |

~10 KB per endpoint; 2,000 connections established in ~110 ms. The default favors latency (every small message sent immediately); enable `SendBatching` for high message rates. These numbers include serialization cost — the library deserializes each inbound message into the handler's typed `T`. Full model, scaling limits and roadmap: [docs/PERFORMANCE.en.md](docs/PERFORMANCE.en.md).

## Documentation

- 📖 **[User guide (docs/GUIDE.en.md)](docs/GUIDE.en.md)** — full usage manual: handlers, transports, reliable channels, reconnect, batching, hardening, the complete `Configuration` reference, and a production checklist. *(Українською: [docs/GUIDE.md](docs/GUIDE.md))*
- ⚙️ **[Performance (docs/PERFORMANCE.en.md)](docs/PERFORMANCE.en.md)** — performance model, scaling limits, structural roadmap. *(Українською: [docs/PERFORMANCE.md](docs/PERFORMANCE.md))*
- 🏗️ **[CLAUDE.md](CLAUDE.md)** / **[AGENTS.md](AGENTS.md)** — architecture overview for contributors and coding agents.

## Build & test

```bash
dotnet build                                              # build (library targets netstandard2.1)
dotnet test SetNet.UnitTests/SetNet.UnitTests.csproj      # 78 unit + integration tests
dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>   # in-process transport scenarios
dotnet run --project SetNet.Tests -- bench                # throughput / connection benchmark

# chat example (two terminals)
dotnet run --project examples/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 alice
```

## Project structure

```
SetNet/             core library (transport abstraction, reliability, hardening) — no serializer dependency
SetNet.MessagePack/ MessagePack ISerializer adapter (companion package)
SetNet.Tests/       in-process scenario harness + benchmark
SetNet.UnitTests/   xUnit unit + integration tests
examples/           runnable chat (Chat.Shared / Chat.Server / Chat.Client)
docs/               GUIDE(.en).md, PERFORMANCE(.en).md
```

## Status

SetNet has been through extensive adversarial auditing (multi-round correctness convergence + a performance pass) with a full unit/integration suite and in-process scenarios. It is well-suited as the **network layer for .NET ↔ .NET real-time systems** (multiplayer games, chat, collaborative apps).

It is **not** a general-purpose RPC/HTTP framework: there is no request/response correlation, no DI/hosting integration, and no WebSocket/browser transport. Before production, implement authentication, set the hardening config, and run a soak/load test under realistic traffic.

## License

MIT — see [LICENSE](LICENSE).
