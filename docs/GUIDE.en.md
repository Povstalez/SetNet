# SetNet — detailed user guide

A complete guide to using the library: from "hello world" to a production configuration.
A short overview is in the [README](../README.md); performance and scaling limits are in [PERFORMANCE.md](PERFORMANCE.en.md).

## Table of contents
1. [Requirements and installation](#1-requirements-and-installation)
2. [Core concepts](#2-core-concepts)
3. [Quick start](#3-quick-start)
4. [Messages and handlers](#4-messages-and-handlers)
5. [Transports: TCP / UDP / Both](#5-transports-tcp--udp--both)
6. [Delivery and reliable channels](#6-delivery-and-reliable-channels)
7. [Disconnects, reconnect, heartbeat](#7-disconnects-reconnect-heartbeat)
8. [Performance and processing order](#8-performance-and-processing-order)
9. [Production hardening](#9-production-hardening)
10. [Metrics](#10-metrics)
11. [Utilities: GameLoopScheduler, EventManager](#11-utilities)
12. [Full Configuration reference](#12-full-configuration-reference)
13. [Production checklist](#13-production-checklist)
14. [Common mistakes](#14-common-mistakes)

---

## 1. Requirements and installation

- **Library**: .NET Standard 2.1 (consumed by .NET Core 3.0+/.NET 5-8, Unity, Mono, Xamarin/MAUI — **not** .NET Framework).
- **Consumers/tests/examples**: .NET 8.

```bash
# locally — a project reference
dotnet add reference ../SetNet/SetNet.csproj
# serializer (the core does not include one) — e.g. the MessagePack adapter:
dotnet add reference ../SetNet.MessagePack/SetNet.MessagePack.csproj
```

> ℹ️ The `SetNet` core **does not include a built-in serializer**. Add `SetNet.MessagePack` (or your own `ISerializer`) and assign it at startup — see [section 4](#4-messages-and-handlers).

---

## 2. Core concepts

| Type | Role |
|---|---|
| `BaseServer` | Listens for connections, creates a `BasePeer` for each client. You subclass it and implement `OnNewClient`. |
| `BasePeer` | The server-side representation of a single client: receives its messages and replies. |
| `BaseClient` | The client: connects, manages the lifecycle (connect/heartbeat/reconnect), receives messages. |
| `Configuration` | All settings (host, port, transport, limits, TLS…). |
| `[MessageHandler(type)]` | An attribute on a handler class; registered via reflection. |

**Message flow:** `SendAsync<T>` → serialization ([your `ISerializer`](#4-messages-and-handlers); e.g. MessagePack) → framing → transport → reassembly → deserialization → handler.

> ⚠️ **Processing order is not guaranteed by default**, even over TCP (handlers are fire-and-forget). See [section 8](#8-performance-and-processing-order).

---

## 3. Quick start

### Step 1. Message types

```csharp
public enum MessageTypes : ushort
{
    PlayerMove = 1,
    ChatMessage = 2,
}

[MessagePackObject]
public class PlayerMoveMessage
{
    [Key(0)] public float X { get; set; }
    [Key(1)] public float Y { get; set; }
}
```

> When using the MessagePack serializer, DTOs **must** be `[MessagePackObject]` with `[Key(n)]` on every field (or `[MessagePackObject(true)]` for key-as-name). For a different serializer, its own requirements apply — see [section 4](#4-messages-and-handlers).

### Step 2. Server

```csharp
using SetNet.Core;
using SetNet.Config;

public class GamePeer : BasePeer
{
    public GamePeer(PeerInfo info) : base(info) { }
    protected override void OnDisconnected() => Console.WriteLine($"{CurrentPeerInfo.Id} left");
    protected override void OnError(string e) => Console.WriteLine(e);
    public Task PushAsync<T>(ushort type, T msg) => SendAsync(type, msg); // public wrapper over the protected SendAsync
}

public class GameServer : BaseServer
{
    public GameServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new GamePeer(peerInfo);
    // StartReceive() is called by the framework itself — not needed manually (but calling it is safe and idempotent)
}

var config = new Configuration { Host = "0.0.0.0", Port = 5682 };
var server = new GameServer(config);
await server.StartAsync(); // long-running accept loop
```

### Step 3. Client

```csharp
public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }

    protected override void OnConnected()    => Console.WriteLine("Connected");
    protected override void OnDisconnected() => Console.WriteLine("Disconnected");
    protected override void OnError(string e)=> Console.WriteLine($"Error: {e}");

    public Task MoveAsync(float x, float y)
        => SendAsync((ushort)MessageTypes.PlayerMove, new PlayerMoveMessage { X = x, Y = y });
}

var client = new GameClient(new Configuration { Host = "127.0.0.1", Port = 5682 });
await client.ConnectAsync();
await client.MoveAsync(10, 20);
```

---

## 4. Messages and handlers

Handlers are discovered via reflection at startup — a class with `[MessageHandler]` that implements the appropriate interface.

### Server-side handler

```csharp
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler
{
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var msg = SetNetSerializer.Deserialize<PlayerMoveMessage>(data);
        // process; reply if needed:
        await ((GamePeer)peer).PushAsync((ushort)MessageTypes.PlayerMove, msg);
    }
}
```

### Client-side handler

```csharp
[MessageHandler((ushort)MessageTypes.ChatMessage)]
public class ChatHandler : IClientMessageHandler
{
    public Task HandleAsync(byte[] data)
    {
        var msg = SetNetSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

**If a handler isn't being called** — check: (1) it implements `IServerMessageHandler`/`IClientMessageHandler`; (2) it has `[MessageHandler]` with the correct `ushort`; (3) the type matches what is being sent; (4) the class is in a loaded assembly.

> ℹ️ Handlers are created via `Activator.CreateInstance` (a public parameterless constructor is required) and **reused as a singleton** for all messages of that type. **There is no constructor DI** — resolve services through a static service locator or your own mechanism.

### Serialization — choose the format yourself (MessagePack, JSON, …)

The `SetNet` core **does not include a built-in serializer** — you choose the format through the `ISerializer` interface (`SetNet.Messaging`):

```csharp
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T      Deserialize<T>(byte[] data);
}
```

Until a serializer is assigned, `SetNetSerializer.Serialize/Deserialize` throw an `InvalidOperationException` with a hint. Assign it **once at startup**, before connecting.

**Option 1 — MessagePack (recommended)** via the separate `SetNet.MessagePack` package. It provides `MessagePackNetSerializer`, hardened with the `UntrustedData` security profile (protection against DoS during deserialization):

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Default = new MessagePackNetSerializer();  // globally, at startup
```

**Option 2 — your own format** (e.g. System.Text.Json), with no dependencies:

```csharp
using SetNet.Messaging;
using System.Text.Json;

public sealed class MyJsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data)!;
}

SetNetSerializer.Default = new MyJsonSerializer();
```

Or **per connection** via config (handy when different servers/clients use different formats):

```csharp
var config = new Configuration { /* ... */ Serializer = new MessagePackNetSerializer() };
```

**Rules:**
- In handlers, deserialize through the `SetNetSerializer.Deserialize<T>(data)` facade — it honors `SetNetSerializer.Default` and does not tie your code to a specific format. (A handler has no reference to the connection, so it cannot see the per-connection serializer.) On the server, if you use a **per-connection** serializer, deserialize through `peer.CurrentPeerInfo.Config.Serializer.Deserialize<T>(data)`.
- `Configuration.Serializer` with no explicit value falls back to `SetNetSerializer.Default`.
- **Both ends** of a connection must use the same format.
- DTO requirements are dictated by the chosen serializer: for MessagePack — `[MessagePackObject]`/`[Key]` (see above); System.Text.Json works with ordinary public properties.

---

## 5. Transports: TCP / UDP / Both

Selected via `Configuration.TransportType` (default `Tcp` — existing TCP code works unchanged).

```csharp
var config = new Configuration
{
    Host = "127.0.0.1", Port = 5682,
    TransportType = TransportType.Both,   // Tcp | Udp | Both
    UdpReliabilityEnabled = true,
    DefaultDelivery = DeliveryMethod.Reliable
};
```

**Routing by `(TransportType, DeliveryMethod)`:**

| TransportType | DeliveryMethod | Channel |
|---|---|---|
| Tcp  | any | TCP |
| Udp  | Reliable | UDP reliability layer (requires `UdpReliabilityEnabled`, otherwise `Validate()` throws) |
| Udp  | Unreliable | Raw UDP datagram |
| Both | Reliable | TCP |
| Both | Unreliable | UDP (falls back to TCP until the UDP channel attaches) |

Notes:
- **UDP is an emulated connection**: a handshake assigns identity, a heartbeat detects liveness, so `OnConnected`/`OnDisconnected`/`BasePeer` work just like over TCP.
- **Both**: TCP first, the server hands a UDP token over TCP, and the UDP handshake binds to the same peer. If UDP is unavailable — a graceful fallback to TCP-only.
- **MTU**: datagrams > `UdpMaxDatagramPayload` (1200 B) are rejected; there is no fragmentation.

Try it locally:
```bash
dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
```

---

## 6. Delivery and reliable channels

`SendAsync` has overloads:

```csharp
await SendAsync(type, msg);                              // DefaultDelivery
await SendAsync(type, msg, DeliveryMethod.Unreliable);  // explicit channel
await SendAsync(type, msg, DeliveryMethod.Reliable, channel: 1); // reliable UDP channel 1
```

### Independent reliable UDP channels (`UdpReliableChannels`)

Each channel has its own sequence/ACK/ordering, so a loss on one does not block another:

```csharp
var config = new Configuration
{
    TransportType = TransportType.Udp, UdpReliabilityEnabled = true,
    UdpReliableChannels = 2   // channels 0 and 1 — independent ordered streams
};
await SendAsync(type, movement, DeliveryMethod.Reliable, channel: 0);
await SendAsync(type, chat,     DeliveryMethod.Reliable, channel: 1);
```

> Reliable UDP has a receive window and back-pressure: the sender cannot get ahead of the "oldest gap" by more than `UdpReliableWindowSize` sequences.

---

## 7. Disconnects, reconnect, heartbeat

`BaseClient` distinguishes an intentional `Disconnect()` from an unexpected loss. **`OnDisconnected` fires exactly once** per connection.

```csharp
public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }
    protected override void OnConnected() { }
    protected override void OnDisconnected() { }                  // closed (any reason)
    protected override void OnError(string e) { }                 // only an unexpected error
    protected override void OnUnexpectedDisconnect() { }          // server down / network
    protected override void OnReconnecting(int a, int max) { }    // before each attempt
    protected override void OnReconnected() { }                   // success
    protected override void OnReconnectFailed() { }               // all attempts exhausted
    protected override void OnStateChanged(ConnectionState f, ConnectionState t) { }
}
```

| Event | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| `Disconnect()` (intentional) | ❌ | ❌ | ✅ | ❌ |
| Network error / server crash | ✅ | ✅ | ✅ (if reconnect failed) | ✅ (if enabled) |
| Graceful close by the server | ❌ | ❌ | ✅ | ❌ |

Auto-reconnect:
```csharp
var config = new Configuration
{
    AutoReconnect = true, MaxReconnectAttempts = 5, ReconnectDelayMs = 1000
};
```

**Heartbeat** (detecting "dead" connections) — **disabled by default**:
```csharp
var config = new Configuration { HeartbeatEnabled = true, HeartbeatIntervalMs = 5000, HeartbeatTimeoutMs = 15000 };
```

On the server, `BasePeer` is symmetric: `Close()` (kick) → only `OnDisconnected`; a client crash/IO error → `OnError` + `OnUnexpectedDisconnect` + `OnDisconnected`.

---

## 8. Performance and processing order

All flags below are opt-in (the default preserves the original behavior).

### Processing order (`SequentialDispatch`)
> ⚠️ Default: handlers are fire-and-forget, **order is not guaranteed even over TCP**.

```csharp
var config = new Configuration { SequentialDispatch = true };
// the receive loop waits for each handler to finish before the next frame (strict order, less parallelism)
```

### Back-pressure (`MaxInFlightMessages`)
```csharp
var config = new Configuration { MaxInFlightMessages = 256 };
// limit on concurrent handlers per connection; when reached, the receive loop is paused
```

### Batching (`SendBatching`) — for the game tick
```csharp
var config = new Configuration { SendBatching = true, SendBatchFlushMs = 15 };
// ... per tick:
await SendAsync(t1, m1);
await SendAsync(t2, m2);   // accumulated into a buffer
await FlushAsync();        // a single write to the socket (on BaseClient/BasePeer)
```
Delivers the highest throughput (~1.8M msgs/sec vs ~240k without batching).

### Send timeout (`SendTimeoutMs`, default 30000)
Bounds the time of a single socket write — a "stuck" peer does not block sending forever. `0` disables it.

### Nagle (`TcpNoDelay`, default `true`)
Nagle disabled = low latency for small frames. For a bulk stream of unbatched messages, `false` gives higher throughput (but +latency). Details are in [PERFORMANCE.md](PERFORMANCE.en.md).

---

## 9. Production hardening

```csharp
using System.Security.Cryptography.X509Certificates;

var config = new Configuration
{
    Host = "0.0.0.0", Port = 5682,

    // TLS over TCP (UDP is NOT encrypted)
    UseSsl = true,
    ServerCertificate = new X509Certificate2("server.pfx", "password"), // on the server
    // on the client: SslTargetHost / ServerCertificateValidationCallback

    // Limits / DoS protection
    MaxConnectionsLimit = 5000,
    MaxUdpPeers = 5000,
    MaxMessageSize = 1024 * 1024,
    MaxConnectionsPerIpPerSecond = 20,
    MaxInFlightMessages = 256,
    MaxInboundQueue = 16384,   // inbound-queue cap per connection (OOM protection)
};
```

- **Authentication is on the application side**: validate credentials in your `OnNewClient`/handlers (the library only provides transport).
- **UDP has no encryption or per-packet authentication** — send sensitive data only over TLS-over-TCP (or Both with reliable, which goes over TCP).
- **Resilience**: a failure in `OnNewClient`/`StartReceive` does not kill the accept loop; a malformed TLS handshake does not take down the server; reconnect/heartbeat errors are logged; an exception in a handler/user hook does not break cleanup; a bounded inbound queue protects against OOM.

---

## 10. Metrics

```csharp
var m = config.Metrics; // NetworkMetrics, thread-safe counters
Console.WriteLine(m.Snapshot()); // sent/recv/accepted/rejected/retransmits/acks/handshakesDropped/inboundDropped
int live = server.ActiveConnections;
```

Most useful for production: `InboundDropped` (overload), `ConnectionsRejected` (limits/rate-limit), `ReliableRetransmits` (UDP losses), `HandshakesDropped` (UDP flood).

---

## 11. Utilities

### GameLoopScheduler — periodic tasks
```csharp
using SetNet.Utils;
var scheduler = new GameLoopScheduler();
scheduler.Every(100, async () => { /* server tick */ await Task.CompletedTask; });
scheduler.StartInBackground();
// await scheduler.StopAsync();
```

### EventManager — in-process pub/sub
```csharp
using SetNet.Events;
var ev = new EventManager();
ev.Subscribe("PlayerJoined", data => { /* ... */ });
ev.Trigger("PlayerJoined", "Alex");
```
> ⚠️ `EventManager` is in-process and **not thread-safe**; it is not a networked pub/sub. To invoke it from multiple threads, synchronize it yourself.

---

## 12. Full Configuration reference

| Option | Default | Purpose |
|---|---|---|
| `Host` / `Port` | — | Endpoint (TCP; UDP too, if `UdpPort=0`). |
| `BufferSize` | 4096 | Read buffer size. |
| `TcpNoDelay` | `true` | Disable Nagle (low latency). |
| `TransportType` | `Tcp` | `Tcp` \| `Udp` \| `Both`. |
| `DefaultDelivery` | `Reliable` | For the 2-arg `SendAsync(type, msg)`. |
| `UdpPort` | 0 | 0 = use `Port`. |
| `UdpReliabilityEnabled` | `true` | Master toggle for reliable UDP. |
| `UdpReliableChannels` | 1 | Number of independent reliable channels. |
| `UdpReliableWindowSize` | 64 | Window (1..64). |
| `UdpReliableAckTimeoutMs` | 100 | Timeout before retransmit. |
| `UdpReliableMaxRetransmits` | 10 | Retransmit cap → onFailure. |
| `UdpMaxDatagramPayload` | 1200 | Max datagram (no fragmentation). |
| `UdpOrderedReliable` | `true` | Ordered reliable delivery. |
| `UdpHandshakeTimeoutMs` | 5000 | UDP handshake timeout. |
| `UdpPeerExpiryMs` | 15000 | Idle time before a UDP peer is removed. |
| `HeartbeatEnabled` | `false` | Ping/Pong to detect dead connections. |
| `HeartbeatIntervalMs` / `HeartbeatTimeoutMs` | 5000 / 15000 | Heartbeat interval / timeout. |
| `AutoReconnect` | `false` | Client auto-reconnect. |
| `MaxReconnectAttempts` / `ReconnectDelayMs` | 3 / 1000 | Reconnect policy. |
| `ConnectTimeoutMs` | 10000 | Connect/handshake timeout. |
| `MaxInFlightMessages` | 0 | Back-pressure (0 = unlimited). |
| `SequentialDispatch` | `false` | Strict processing order. |
| `SendBatching` / `SendBatchFlushMs` | `false` / 15 | Coalesced TCP write. |
| `SendTimeoutMs` | 30000 | Limit on a single socket write (0 = off). |
| `MaxInboundQueue` | 16384 | Inbound-queue cap (OOM protection). |
| `UseSsl` | `false` | TLS over TCP. |
| `ServerCertificate` / `SslTargetHost` / `ServerCertificateValidationCallback` | null | TLS parameters. |
| `MaxConnections` | 100 | Base connection cap. |
| `MaxConnectionsLimit` | 0 | If >0 — overrides `MaxConnections`. |
| `MaxUdpPeers` | 1000 | UDP peer cap. |
| `MaxMessageSize` | 1 MiB | TCP frame cap. |
| `MaxConnectionsPerIpPerSecond` | 0 | Per-IP rate limit (0 = off). |
| `Logger` | `ConsoleLogger` | Logging (`ILogger`). |
| `Metrics` | — | `NetworkMetrics` counters. |

`Validate()` is called on connect/start and fail-fast checks incompatible settings.

---

## 13. Production checklist

The defaults are optimized for compatibility, not for production. Before launch:

- [ ] Implement **authorization** in `OnNewClient`/handlers.
- [ ] `HeartbeatEnabled = true` (otherwise dead connections are not detected).
- [ ] `MaxInFlightMessages > 0` (otherwise unbounded fire-and-forget Tasks under load).
- [ ] `MaxConnectionsLimit`, `MaxConnectionsPerIpPerSecond` tuned to your capacity.
- [ ] `UseSsl = true` + a certificate, if outside a trusted network (and do **not** send sensitive data over UDP).
- [ ] Export `config.Metrics.Snapshot()` to monitoring.
- [ ] **Soak/load test** with real traffic before a full launch.

Detailed scaling limits are in [PERFORMANCE.md](PERFORMANCE.en.md).

---

## 14. Common mistakes

| Symptom | Cause / resolution |
|---|---|
| Handler not called | No `[MessageHandler]`, the wrong type, doesn't implement the interface, or the class is not in a loaded assembly. |
| Messages get "corrupted" | Different serializers on the two ends; (MessagePack) a DTO without `[MessagePackObject]`/`[Key]`; or the type doesn't match. |
| `InvalidOperationException: No serializer configured` | `SetNetSerializer.Default` not assigned — do it at startup (see section 4). |
| Won't connect | Host/Port differ on the client and server; firewall; (UDP) handshake is blocked. |
| Out-of-order processing | This is the default behavior — enable `SequentialDispatch`. |
| Reliable UDP throws on send | `DefaultDelivery=Reliable` + `UdpReliabilityEnabled=false` on plain UDP. Validate() catches this. |
| OOM under flood | Check `MaxInboundQueue`, `MaxUdpPeers`, `MaxMessageSize`, `MaxConnectionsPerIpPerSecond`. |

---

A full chat example (separate server and client) is in the [`examples/`](../examples) folder. Architecture and project structure are in [CLAUDE.md](../CLAUDE.md).
