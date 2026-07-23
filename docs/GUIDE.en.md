# SetNet — detailed user guide

A complete guide to using the library: from "hello world" to a production configuration.
A short overview is in the [README](../README.md); performance and scaling limits are in [PERFORMANCE.md](PERFORMANCE.en.md).

## Table of contents
1. [Requirements and installation](#1-requirements-and-installation)
2. [Core concepts](#2-core-concepts)
3. [Quick start](#3-quick-start)
4. [Messages and handlers](#4-messages-and-handlers)
5. [Unified protocol: request/reply, push, RPC](#5-unified-protocol-requestreply-push-rpc)
6. [Transports: TCP / UDP / Both](#6-transports-tcp--udp--both)
7. [Delivery and reliable channels](#7-delivery-and-reliable-channels)
8. [Disconnects, reconnect, heartbeat](#8-disconnects-reconnect-heartbeat)
9. [Performance and processing order](#9-performance-and-processing-order)
10. [Production hardening](#10-production-hardening)
11. [Metrics](#11-metrics)
12. [Utilities: GameLoopScheduler, EventManager](#12-utilities)
13. [Full Configuration reference](#13-full-configuration-reference)
14. [Production checklist](#14-production-checklist)
15. [Common mistakes](#15-common-mistakes)

---

## 1. Requirements and installation

- **Library**: .NET Standard 2.1 (consumed by .NET Core 3.0+/.NET 5-8, Unity, Mono, Xamarin/MAUI — **not** .NET Framework).
- **Consumers/tests/examples**: .NET 8.

```bash
dotnet add package SetNet
# serializer (the core does not include one) — e.g. the MessagePack adapter:
dotnet add package SetNet.MessagePack
```

> ℹ️ The `SetNet` core **does not include a built-in serializer**. Add `SetNet.MessagePack` (or your own `ISerializer`) and register it at startup — see [section 4](#4-messages-and-handlers).

---

## 2. Core concepts

| Type | Role |
|---|---|
| `BaseServer` | Listens for connections, creates a `BasePeer` for each client. You subclass it and implement `OnNewClient`. |
| `BasePeer` | The server-side representation of a single client: receives its messages and replies. |
| `BaseClient` | The client: connects, manages the lifecycle (connect/heartbeat/reconnect), receives messages. |
| `Configuration` | All settings (host, port, transport, limits, TLS…). |
| `[MessageHandler(type)]` | An attribute on a handler class for **one-way** messages; registered via reflection ([section 4](#4-messages-and-handlers)). |
| `[ProtocolChannel(channel)]` | An attribute on a class that serves one **channel** of the unified protocol (server ops, or client push handlers) — [section 5](#5-unified-protocol-requestreply-push-rpc). |
| `[Op(op)]` | An attribute on a method that handles one channel operation (**request/reply** or fire-and-forget). |
| `[Event(op)]` | An attribute on a client-side method that handles one **server push** event. |
| `[RpcMethod(id)]` | An attribute on an `IRpcHandler<TReq,TResp>` — the method-style front end (`SetNet.Rpc`). |

**Message flow:** `SendAsync<T>` → serialization ([your `ISerializer`](#4-messages-and-handlers); e.g. MessagePack) → framing → transport → reassembly → deserialization → handler.

> ⚠️ **Processing order is not guaranteed by default**, even over TCP (handlers are fire-and-forget). See [section 9](#9-performance-and-processing-order).

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

Handlers are discovered via reflection by default, or registered explicitly on `SetNetRuntime.Handlers`. A handler is a class with `[MessageHandler]` that implements `IServerMessageHandler<T>` or `IClientMessageHandler<T>`. Handlers are **strongly typed**: the library deserializes the payload and hands you the ready `T` — no manual deserialization.

> `[MessageHandler]` is the **one-way** message kind (fire it, handle it, nothing comes back). For request/reply, channel ops, server-push events and RPC, see [section 5](#5-unified-protocol-requestreply-push-rpc).

### Server-side handler

```csharp
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;

[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler<PlayerMoveMessage>
{
    public async Task HandleAsync(BasePeer peer, PlayerMoveMessage msg)
    {
        // process; reply if needed:
        await ((GamePeer)peer).PushAsync((ushort)MessageTypes.PlayerMove, msg);
    }
}
```

### Client-side handler

```csharp
[MessageHandler((ushort)MessageTypes.ChatMessage)]
public class ChatHandler : IClientMessageHandler<ChatMessage>
{
    public Task HandleAsync(ChatMessage msg)
    {
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

**If a handler isn't being called** — check: (1) it implements `IServerMessageHandler<T>`/`IClientMessageHandler<T>`; (2) it has `[MessageHandler]` with the correct `ushort` or was registered explicitly on `SetNetRuntime.Handlers`; (3) both `T` and the `ushort` match what is being sent; (4) the handler's assembly is loaded or registered with the runtime.

> ℹ️ By default, handlers are created via `Activator.CreateInstance` (a public parameterless constructor is required) and **reused as a singleton** for all messages of that type. Use `SetNet.DependencyInjection` when you want constructor injection.

### Serialization — choose the format yourself (MessagePack, JSON, …)

The `SetNet` core **does not include a built-in serializer** — you choose the format through the `ISerializer` interface (`SetNet.Messaging`):

```csharp
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T      Deserialize<T>(byte[] data);
}
```

Until a serializer is assigned, typed sends and typed handler dispatch throw an `InvalidOperationException` with a hint. Assign it before connecting or starting a server. `SetNetSerializer.Use(...)` configures the backward-compatible `SetNetRuntime.Default`; for isolated environments, put a custom `SetNetRuntime` on `Configuration.Runtime`.

**Option 1 — MessagePack (recommended)** via the separate `SetNet.MessagePack` package. It provides `MessagePackNetSerializer`, hardened with the `UntrustedData` security profile (protection against DoS during deserialization):

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Use(new MessagePackNetSerializer());  // globally, at startup
```

Scoped runtime:

```csharp
using SetNet;
using SetNet.Config;
using SetNet.MessagePack;

var runtime = new SetNetRuntime()
    .UseSerializer(new MessagePackNetSerializer());

runtime.Handlers.AutoDiscoverLoadedAssemblies = false;
runtime.Handlers.AddHandlersFromAssemblyOf<PlayerMoveHandler>();

var serverConfig = ConfigurationPresets.Development("0.0.0.0", 5000);
serverConfig.Runtime = runtime;

var clientConfig = ConfigurationPresets.Development("127.0.0.1", 5000);
clientConfig.Runtime = runtime;
```

Use a scoped runtime when integration tests run several SetNet stacks in one process, when a plugin host needs explicit handler catalogs, or when two listeners need different serializers/handler sets. In separate processes, create matching runtimes on both sides.

**Option 2 — your own format** (e.g. System.Text.Json), with no dependencies:

```csharp
using SetNet.Messaging;
using System.Text.Json;

public sealed class MyJsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data)!;
}

SetNetSerializer.Use(new MyJsonSerializer());
```

**Rules:**
- The serializer is **one per runtime**. If you do nothing special, the app uses `SetNetRuntime.Default` via `SetNetSerializer.Use(...)`. If you set `Configuration.Runtime`, that endpoint uses the runtime's serializer and handler registry.
- Handlers are **strongly typed** — they receive the ready `T`; no manual deserialization (the library does it). `SetNetSerializer.Serialize/Deserialize` remain available for ad-hoc needs on the default runtime; scoped code can call `runtime.Serialize/Deserialize`.
- **Both ends** of a connection must use the same format.
- DTO requirements are dictated by the chosen serializer: for MessagePack — `[MessagePackObject]`/`[Key]` (see above); System.Text.Json works with ordinary public properties.

### Raw frame access — relay/proxy (`OnRawFrame` + `SendRawAsync`)

Sometimes a frame should be **forwarded without looking inside** — e.g. an Among Us-style relay server that shuttles game traffic between players in a lobby. Deserializing then re-serializing there is pure waste. Two primitives on `BaseClient`/`BasePeer` cover this:

```csharp
// override on BaseSocket: called for EVERY application frame (system Ping/Pong/BindToken are excluded),
// BEFORE typed dispatch. Return true = "consumed", the typed handler is skipped.
protected virtual bool OnRawFrame(ushort type, byte[] data);

// send already-serialized bytes WITHOUT serializing
protected Task SendRawAsync(ushort type, byte[] payload, DeliveryMethod? delivery = null);
```

A relay peer forwards the raw bytes and consumes the frame (zero deserialization):

```csharp
public class RelayPeer : BasePeer
{
    private readonly RelayServer _server;
    public RelayPeer(PeerInfo info, RelayServer server) : base(info) { _server = server; }

    // public wrapper so the server's broadcast loop can forward into this peer
    public Task ForwardAsync(ushort type, byte[] data) => SendRawAsync(type, data, DeliveryMethod.Unreliable);

    protected override bool OnRawFrame(ushort type, byte[] data)
    {
        _server.BroadcastRawToLobby(LobbyId, type, data, except: CurrentPeerInfo.Id);  // your policy
        return true;  // do not pass to a typed handler
    }
}
// BroadcastRawToLobby iterates the lobby's peers and calls peer.ForwardAsync(type, data)
```

**Rules:**
- `return false` (the default) → the frame continues to its typed handler. Normal code doesn't override `OnRawFrame` and **pays nothing** (an empty virtual call).
- `return true` → typed dispatch is skipped. **No deserialization** happens at all.
- Hybrid is fine: control messages (join/ready/kick) via typed handlers, game traffic via `OnRawFrame` + `SendRawAsync`. Branch on `type` inside.
- `OnRawFrame` runs synchronously on the receive path — forward fire-and-forget (`_ = SendRawAsync(...)`) or batch; don't block.

---

## 5. Unified protocol: request/reply, push, RPC

Section 4 covers **one-way** messages: you send, the other side handles them, nothing comes back. Everything else —
"ask the server and await the answer", "tell the server, no answer needed", "the server pushes an event to clients" —
goes through the **unified protocol** (namespace `SetNet.Protocol`, part of the core — nothing to install).

It occupies a **single** reserved wire type (`65447`) and demultiplexes inside it by a **channel** (`ushort`, e.g. your
own `World = 1000`) plus an **op** (`ushort`) within that channel. Every shipped module (Rooms, Inventory, Chat, …)
speaks exactly this, so your own channels look and behave like theirs.

### 5.1 Which kind of message do I need?

| I want to… | Client side | Server side |
|---|---|---|
| Send a one-way message | `SendAsync<T>(type, msg)` | `[MessageHandler]` + `IServerMessageHandler<T>` (section 4) |
| Receive a one-way message | `[MessageHandler]` + `IClientMessageHandler<T>` | `peer.SendAsync<T>(type, msg)` |
| **Ask and await an answer** | `RequestAsync<TReq,TResp>(channel, op, req)` | `[ProtocolChannel]` + `[Op]` method that **returns** the reply |
| **Tell the server, no answer** | `PostAsync<T>(channel, op, msg)` | `[Op]` method returning `void`/`Task` |
| **Receive a server push** | `On<T>(channel, op, …)` or an `[Event]` method | `peer.PublishAsync(channel, op, evt)` |
| **Method-style call** | `CallAsync<TReq,TResp>(methodId, req)` (`SetNet.Rpc`) | `[RpcMethod]` + `IRpcHandler<TReq,TResp>` |

All of these run **together on one connection**; the complete map of layers and modules is in
[COMMUNICATION.md](COMMUNICATION.md).

### 5.2 Step 1 — shared contracts

Ids and DTOs must match on both ends, so declare them once in a shared assembly:

```csharp
using MessagePack;

public static class GameChannels
{
    public const ushort World = 1000;                    // your own channel id — see 5.9
}

public enum WorldOp  : ushort { Drop = 1, Ready = 2 }    // client → server
public enum WorldEvt : ushort { ItemDropped = 10 }       // server → client

[MessagePackObject]
public class DropReq  { [Key(0)] public string ItemId { get; set; } = ""; [Key(1)] public int Count { get; set; } }

[MessagePackObject]
public class DropResp { [Key(0)] public bool Ok { get; set; } [Key(1)] public int Left { get; set; } }

[MessagePackObject]
public class ItemDropped { [Key(0)] public int PlayerId { get; set; } [Key(1)] public string ItemId { get; set; } = ""; }
```

### 5.3 Server — one method per op (`[ProtocolChannel]` + `[Op]`)

The everyday style: a plain class marked with the channel id and one method per operation — no `switch`, no base
class, no manual registration (discovered by reflection, like `[MessageHandler]`).

```csharp
using SetNet.Core;
using SetNet.Protocol;

[ProtocolChannel(GameChannels.World)]
public sealed class WorldChannel
{
    // request → reply: the RETURN VALUE is the reply
    [Op((ushort)WorldOp.Drop)]
    public async Task<DropResp> Drop(BasePeer peer, DropReq req)
    {
        if (req.Count <= 0) throw new ProtocolException("Count must be positive.");   // → error on the caller

        var left = await Game.TryTakeAsync(peer, req.ItemId, req.Count);              // your authoritative logic
        if (left < 0) throw new ProtocolException("Not enough items.");

        return new DropResp { Ok = true, Left = left };   // (to also notify other players — see 5.6)
    }

    // fire-and-forget: void/Task sends nothing back (this is what PostAsync targets)
    [Op((ushort)WorldOp.Ready)]
    public void Ready(BasePeer peer) => Game.MarkReady(peer);

    // raw in, raw out — the serializer is not involved at all
    [Op(99)]
    public byte[] Echo(byte[] body) => body;
}
```

**Parameters** are bound **by type**, in any order, all of them optional:

| Parameter type | Bound to |
|---|---|
| `BasePeer` | the peer that sent the message |
| `ChannelRequest` | the full request context (see 5.4) |
| `byte[]` | the raw, undeserialized body |
| anything else | the body, deserialized via your serializer (at most one such parameter) |

**Return value → reply:**

| Return | Effect |
|---|---|
| `T` / `Task<T>` | serialized and sent as the reply |
| `byte[]` / `Task<byte[]>` | sent as the reply verbatim (no serialization) |
| `void` / `Task` | no reply at all — for fire-and-forget ops (or reply yourself via a `ChannelRequest` parameter) |
| throws | the caller's `RequestAsync` throws `ProtocolException` carrying your message |

### 5.4 Server — full control (`IChannelService`)

When you want a single entry point for the whole channel (shared setup, custom routing, ops decided at runtime),
implement `IChannelService` instead. A class that implements it keeps manual control, and its `[Op]` methods — if any —
are **ignored**.

```csharp
[ProtocolChannel(GameChannels.World)]
public sealed class WorldService : IChannelService
{
    public async Task HandleAsync(ChannelRequest r)
    {
        switch ((WorldOp)r.Op)
        {
            case WorldOp.Drop:
                var req = r.Read<DropReq>();                        // typed body … or r.RawBody for bytes
                await r.ReplyAsync(new DropResp { Ok = true });     // typed reply … or r.ReplyRawAsync(bytes)
                break;

            case WorldOp.Ready:
                Game.MarkReady(r.Peer);                             // fire-and-forget: no reply
                break;

            default:
                if (r.ExpectsReply) await r.ReplyErrorAsync($"Unknown op {r.Op}");
                break;
        }
    }
}
```

`ChannelRequest`: `Peer`, `Channel`, `Op`, `RawBody`, `ExpectsReply`, `Read<T>()`, `ReplyAsync<T>(T)`,
`ReplyRawAsync(byte[])`, `ReplyErrorAsync(string)`. Reply **at most once** — later reply calls are ignored.

### 5.5 Client — request and post

```csharp
using SetNet.Protocol;

// request → reply (correlated, always Reliable, with a timeout)
DropResp resp = await client.RequestAsync<DropReq, DropResp>(
    GameChannels.World, (ushort)WorldOp.Drop,
    new DropReq { ItemId = "sword", Count = 1 },
    timeoutMs: 10000);                       // default 10 s; ≤ 0 = wait indefinitely; also takes a CancellationToken

byte[] raw = await client.RequestRawAsync(GameChannels.World, 99, new byte[] { 1, 2, 3 });   // serializer-agnostic

// fire-and-forget — the only form where you choose reliability
await client.PostAsync(GameChannels.World, (ushort)WorldOp.Ready, new ReadyDto());
await client.PostRawAsync(GameChannels.World, (ushort)WorldOp.Ready, bytes, DeliveryMethod.Unreliable);
```

### 5.6 Server push and client subscription

Server side — push to one peer or to many:

```csharp
await peer.PublishAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, evt);       // one client, typed
await peer.PublishRawAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, bytes);  // one client, raw

IEnumerable<BasePeer> others = server.OthersInRoomOf(peer);   // SetNet.Rooms helper — or your own peer list
await others.PublishAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, evt);     // fan-out, best-effort
```

Client side — two styles, and **both** fire for the same `(channel, op)`:

```csharp
// (a) imperative — can close over state, returns an IDisposable to unsubscribe
IDisposable sub = client.On<ItemDropped>(GameChannels.World, (ushort)WorldEvt.ItemDropped, e => Render(e));
client.OnRaw(GameChannels.World, 99, bytes => { /* decode it yourself */ });
// sub.Dispose();   // unsubscribe

// (b) declarative — a [ProtocolChannel] class with [Event] methods, auto-subscribed on the first event
[ProtocolChannel(GameChannels.World)]
public sealed class WorldEvents
{
    [Event((ushort)WorldEvt.ItemDropped)] public void OnDropped(ItemDropped e) => Render(e);
    [Event(99)]                           public void OnBlob(byte[] body)      { /* raw body */ }
}
```

An `[Event]` method takes the typed body, a `byte[]`, or no parameter at all, and returns `void` or `Task` (async
handlers run fire-and-forget; an exception in one is isolated). Its instances are **process-wide singletons** — use
style (a) when the handler must close over per-instance state (a driver holding room state, say).

### 5.7 Errors and timeouts

| On the server | On the caller (`RequestAsync`) |
|---|---|
| the handler throws (any exception) | `ProtocolException` carrying the exception message |
| `throw new ProtocolException("…")` | the same — the deliberate way to fail a request |
| no `[Op]` matches the op | `ProtocolException("No [Op(N)] handler on channel C.")` |
| no channel service for that channel | `ProtocolException("No protocol channel C is configured on this server.")` |
| the op never replies (returns `void`/`Task`) | `TimeoutException` after `timeoutMs` |

A fire-and-forget `PostAsync` to an unknown op is silently ignored — nobody is waiting for it. Shipped modules re-map
`ProtocolException` to their own type (`RoomException`, `RpcException`, …); do the same in your own client driver if
you want a domain-specific error.

### 5.8 RPC — a typed alias of `RequestAsync` (`SetNet.Rpc`)

If a method-id front end reads better than channel + op: `client.CallAsync<TReq,TResp>(id, req)` **is**
`client.RequestAsync<TReq,TResp>(Channels.Rpc, id, req)` — the same envelope and correlation, with `RpcException`
instead of `ProtocolException` and a default timeout of 5 s.

```bash
dotnet add package SetNet.Rpc
```

```csharp
using SetNet.Rpc;

RpcRuntime.Enable();      // once at startup, on both ends

// client
var resp = await client.CallAsync<LoginReq, LoginResp>(1, new LoginReq { Name = "alice" });

// server
[RpcMethod(1)]
public class LoginHandler : IRpcHandler<LoginReq, LoginResp>
{
    public Task<LoginResp> HandleAsync(BasePeer peer, LoginReq req)
        => Task.FromResult(new LoginResp { Ok = true });
}
```

### 5.9 Rules and gotchas

- **Channel ids from ~1000 up are yours.** 1–34 belong to the shipped modules (`SetNet.Protocol.Channels`). The channel
  space is independent of the core `ushort` message-type space, so `GameChannels.World = 1000` and
  `MessageTypes.PlayerMove = 1` never collide.
- **One service per channel id.** Server `[ProtocolChannel]` classes are found by scanning loaded assemblies (last one
  discovered wins for a channel) — not through `runtime.Handlers`. A class counts as a *server* channel only if it
  implements `IChannelService` or has at least one `[Op]` method; a class with only `[Event]` methods is client-side.
- **Ops are scoped to their channel.** `[Op(1)]` on two different channels are unrelated; duplicates *within* one class
  throw at discovery.
- **Reliability.** `Request*` and `Publish*` are always `Reliable`; only `Post*` lets you pick `Unreliable`.
  High-frequency state (positions, health) belongs in `SetNet.StateSync` or the core layer, not here.
- **Typed vs raw.** Typed overloads use the endpoint's serializer, so `T` must satisfy its rules (MessagePack:
  `[MessagePackObject]`/`[Key]`). The `*Raw*` family (`RequestRawAsync`, `PostRawAsync`, `OnRaw`, `RawBody`,
  `ReplyRawAsync`) is serializer-agnostic — handy for hand-framed control messages.
- **Shipped modules need their `Enable()`.** Call `XxxRuntime.Enable()` once at startup so the module assembly is
  loaded and discoverable. Your own channels, living in your own app assembly, need nothing.
- **Handler instances are singletons**, created through the same activator as `[MessageHandler]` — use
  `SetNet.DependencyInjection` for constructor injection.

---

## 6. Transports: TCP / UDP / Both

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
dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
```

---

## 7. Delivery and reliable channels

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

## 8. Disconnects, reconnect, heartbeat

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

## 9. Performance and processing order

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

## 10. Production hardening

```csharp
using System.Security.Cryptography.X509Certificates;

var config = ConfigurationPresets.ProductionTcp("0.0.0.0", 5682);
config.ServerCertificate = new X509Certificate2("server.pfx", "password");

// Or build it manually:
config = new Configuration
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

foreach (var issue in config.AnalyzeProduction())
    Console.WriteLine(issue);

config.ValidateProduction(); // throws if production-blocking errors remain
```

- **Authentication is on the application side**: validate credentials in your `OnNewClient`/handlers (the library only provides transport).
- **UDP has no encryption or per-packet authentication** — send sensitive data only over TLS-over-TCP (or Both with reliable, which goes over TCP).
- **Resilience**: a failure in `OnNewClient`/`StartReceive` does not kill the accept loop; a malformed TLS handshake does not take down the server; reconnect/heartbeat errors are logged; an exception in a handler/user hook does not break cleanup; a bounded inbound queue protects against OOM.

---

## 11. Metrics

```csharp
var m = config.Metrics; // NetworkMetrics, thread-safe counters
Console.WriteLine(m.Snapshot()); // sent/recv/accepted/rejected/retransmits/acks/handshakesDropped/inboundDropped
int live = server.ActiveConnections;
```

Most useful for production: `InboundDropped` (overload), `ConnectionsRejected` (limits/rate-limit), `ReliableRetransmits` (UDP losses), `HandshakesDropped` (UDP flood).

---

## 12. Utilities

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

## 13. Full Configuration reference

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

## 14. Production checklist

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

## 15. Common mistakes

| Symptom | Cause / resolution |
|---|---|
| Handler not called | No `[MessageHandler]`, the wrong type, doesn't implement the interface, or the class/assembly is not loaded or registered on `SetNetRuntime.Handlers`. |
| `ProtocolException: No protocol channel N is configured on this server` | The `[ProtocolChannel(N)]` class is missing on the server, its assembly isn't loaded (for a shipped module call its `XxxRuntime.Enable()`), or the class neither implements `IChannelService` nor has any `[Op]` method. |
| `RequestAsync` throws `TimeoutException` | The `[Op]` method returns `void`/`Task`, so it never replies (send those with `PostAsync`), the op id doesn't match, or the handler is slower than `timeoutMs`. |
| `On<T>` / `[Event]` handler never fires | The class is missing `[ProtocolChannel]`, the `(channel, op)` doesn't match the server's `PublishAsync`, or the subscription's `IDisposable` was disposed. |
| An `[Event]` handler sees the wrong instance state | `[Event]` handler instances are process-wide singletons — use `client.On<T>(...)` when the handler must close over per-instance state (section 5.6). |
| Messages get "corrupted" | Different serializers on the two ends; (MessagePack) a DTO without `[MessagePackObject]`/`[Key]`; or the type doesn't match. |
| `InvalidOperationException: No serializer configured` | No serializer on the endpoint runtime — call `SetNetSerializer.Use(...)` for the default runtime or `runtime.UseSerializer(...)` before assigning `Configuration.Runtime` (see section 4). |
| Won't connect | Host/Port differ on the client and server; firewall; (UDP) handshake is blocked. |
| Out-of-order processing | This is the default behavior — enable `SequentialDispatch`. |
| Reliable UDP throws on send | `DefaultDelivery=Reliable` + `UdpReliabilityEnabled=false` on plain UDP. Validate() catches this. |
| OOM under flood | Check `MaxInboundQueue`, `MaxUdpPeers`, `MaxMessageSize`, `MaxConnectionsPerIpPerSecond`. |

---

A full chat example (separate server and client) is in the [`examples/`](../examples) folder. Architecture and project structure are in [CLAUDE.md](../CLAUDE.md).
