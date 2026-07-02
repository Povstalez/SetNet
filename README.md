<p align="center">
  <img src="assets/logo.png" alt="SetNet" width="320">
</p>

# SetNet

**A lightweight, high-throughput .NET networking library for client–server games and real-time apps — over TCP, UDP, or both at once.**

[![NuGet — SetNet](https://img.shields.io/nuget/v/SetNet?logo=nuget&label=SetNet)](https://www.nuget.org/packages/SetNet)
[![NuGet — SetNet.MessagePack](https://img.shields.io/nuget/v/SetNet.MessagePack?logo=nuget&label=SetNet.MessagePack)](https://www.nuget.org/packages/SetNet.MessagePack)
[![NuGet — SetNet.Rpc](https://img.shields.io/nuget/v/SetNet.Rpc?logo=nuget&label=SetNet.Rpc)](https://www.nuget.org/packages/SetNet.Rpc)
[![NuGet — SetNet.Auth](https://img.shields.io/nuget/v/SetNet.Auth?logo=nuget&label=SetNet.Auth)](https://www.nuget.org/packages/SetNet.Auth)
[![NuGet — SetNet.Rooms](https://img.shields.io/nuget/v/SetNet.Rooms?logo=nuget&label=SetNet.Rooms)](https://www.nuget.org/packages/SetNet.Rooms)
[![NuGet — SetNet.RateLimit](https://img.shields.io/nuget/v/SetNet.RateLimit?logo=nuget&label=SetNet.RateLimit)](https://www.nuget.org/packages/SetNet.RateLimit)
[![NuGet — SetNet.WebSockets](https://img.shields.io/nuget/v/SetNet.WebSockets?logo=nuget&label=SetNet.WebSockets)](https://www.nuget.org/packages/SetNet.WebSockets)
[![NuGet — SetNet.Unity](https://img.shields.io/nuget/v/SetNet.Unity?logo=nuget&label=SetNet.Unity)](https://www.nuget.org/packages/SetNet.Unity)
[![NuGet — SetNet.Logging.Serilog](https://img.shields.io/nuget/v/SetNet.Logging.Serilog?logo=nuget&label=SetNet.Logging.Serilog)](https://www.nuget.org/packages/SetNet.Logging.Serilog)
[![NuGet — SetNet.InMemory](https://img.shields.io/nuget/v/SetNet.InMemory?logo=nuget&label=SetNet.InMemory)](https://www.nuget.org/packages/SetNet.InMemory)
[![NuGet — SetNet.Matchmaking](https://img.shields.io/nuget/v/SetNet.Matchmaking?logo=nuget&label=SetNet.Matchmaking)](https://www.nuget.org/packages/SetNet.Matchmaking)
[![NuGet — SetNet.StateSync](https://img.shields.io/nuget/v/SetNet.StateSync?logo=nuget&label=SetNet.StateSync)](https://www.nuget.org/packages/SetNet.StateSync)
[![Downloads](https://img.shields.io/nuget/dt/SetNet?logo=nuget&label=downloads)](https://www.nuget.org/packages/SetNet)
![.NET Standard 2.1](https://img.shields.io/badge/.NET%20Standard-2.1-512BD4)
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
- ⚡ **Fast** — ~**1.6M msgs/sec** on one connection with send batching, ~10 KB per endpoint; allocation-light hot paths.
- 🔒 **Production-hardened** — TLS over TCP, connection/UDP-peer caps, per-IP rate limiting, frame-size cap, back-pressure, bounded inbound queues (OOM protection), a resilient accept loop, and live `NetworkMetrics`.
- 🧩 **Auto handler registration** — mark a class `[MessageHandler(type)]`; reflection wires it up. Handlers are **strongly typed** — `IServerMessageHandler<T>`/`IClientMessageHandler<T>` receive the deserialized message; the library (de)serializes for you.
- 🔀 **Raw relay escape hatch** — override `OnRawFrame(type, data)` to intercept frames and `SendRawAsync` to forward bytes **without (de)serializing** — build an Among Us-style relay/proxy with zero overhead, while normal handlers stay typed.
- 📦 **Pluggable serialization** — the core bundles no serializer. Pick a format via `ISerializer`: drop in the `SetNet.MessagePack` package (hardened MessagePack), or supply your own JSON/Protobuf/custom adapter, and register it once with `SetNetSerializer.Use(...)`.
- 📞 **Optional RPC** — add the [`SetNet.Rpc`](https://www.nuget.org/packages/SetNet.Rpc) package for request/response: `await client.CallAsync<TReq, TResp>(...)` + `[RpcMethod]` handlers. Added by composition (no base class), coexists with one-way messages.
- 🔐 **Optional Auth + sessions** — add [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth): an enforced gate drops a peer's traffic until it authenticates (you validate the token via `IAuthenticator`), plus session store, multi-session policy, and automatic reconnect-resume. Composition, over TLS.
- 🏠 **Optional Rooms/Lobbies** — add [`SetNet.Rooms`](https://www.nuget.org/packages/SetNet.Rooms): create/join rooms by code, broadcast within a room, player-joined/left events, auto-leave on disconnect. Dedicated-server (no relay needed), pluggable room store.
- 🎯 **Optional Matchmaking** — add [`SetNet.Matchmaking`](https://www.nuget.org/packages/SetNet.Matchmaking): queue players into FIFO or skill-based matches with a widening acceptance window, then drop each match into a room to join. Built on top of Rooms.
- 🎞️ **Optional state replication** — add [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync): server-authoritative entity replication with delta-compressed snapshots, client interpolation, interest management, and an input channel — engine-agnostic core plus a Unity layer (NetworkObject/NetworkTransform/NetworkAnimator/NetworkRigidbody).
- 🧪 **Optional in-memory transport** — add [`SetNet.InMemory`](https://www.nuget.org/packages/SetNet.InMemory): run a client and server in one process with no sockets (`config.UseInMemory()`) for fast, deterministic integration tests and co-hosting.
- 🌐 **Optional WebSocket transport** — add [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets): run everything over `ws://` (HTTP-friendly, proxy/firewall-traversable) with one `config.UseWebSockets()` call; handlers/RPC/rooms/auth unchanged.
- 🚏 **Optional per-peer rate limiting** — add [`SetNet.RateLimit`](https://www.nuget.org/packages/SetNet.RateLimit): a token-bucket inbound gate (`server.UseRateLimit(...)`) that drops a flooding peer's excess frames; composes with the Auth gate.
- 🎮 **Optional Unity helper** — add [`SetNet.Unity`](https://www.nuget.org/packages/SetNet.Unity): a `MainThreadDispatcher` to marshal handler callbacks onto Unity's main thread (drain in `Update()`).
- 🪵 **Optional Serilog logging** — add [`SetNet.Logging.Serilog`](https://www.nuget.org/packages/SetNet.Logging.Serilog): route SetNet's `ILogger` into Serilog (`config.Logger = new SerilogLogger(...)`).

## Modules

The core is one package (`SetNet`); everything else is an **optional companion** added by composition — pull in only what you need. Each package name links to its **README**; the 📦 links to **NuGet** (install with `dotnet add package <name>`). Full catalog + core extension points: **[docs/MODULES.md](docs/MODULES.md)**.

**Core & serializers**

| Package | What it does |
|---|---|
| [SetNet](src/core/SetNet/README.md) · [📦](https://www.nuget.org/packages/SetNet) | Core: TCP/UDP/Both transport, reliability, hardening, typed handlers |
| [SetNet.MessagePack](src/serializers/SetNet.MessagePack/README.md) · [📦](https://www.nuget.org/packages/SetNet.MessagePack) | Recommended serializer (hardened MessagePack) |
| [SetNet.Json](src/serializers/SetNet.Json/README.md) · [📦](https://www.nuget.org/packages/SetNet.Json) | `System.Text.Json` — human-readable, web-friendly |
| [SetNet.MemoryPack](src/serializers/SetNet.MemoryPack/README.md) · [📦](https://www.nuget.org/packages/SetNet.MemoryPack) | Fast, zero-encoding, AOT/IL2CPP-friendly |
| [SetNet.Protobuf](src/serializers/SetNet.Protobuf/README.md) · [📦](https://www.nuget.org/packages/SetNet.Protobuf) | protobuf-net — compact, cross-language |
| [SetNet.Compression](src/serializers/SetNet.Compression/README.md) · [📦](https://www.nuget.org/packages/SetNet.Compression) | Transparent Brotli decorator over any serializer |

**Transports** (`config.UseXxx()`)

| Package | What it does |
|---|---|
| [SetNet.WebSockets](src/transports/SetNet.WebSockets/README.md) · [📦](https://www.nuget.org/packages/SetNet.WebSockets) | `ws://` transport — HTTP-friendly, proxy/firewall-traversable |
| [SetNet.InMemory](src/transports/SetNet.InMemory/README.md) · [📦](https://www.nuget.org/packages/SetNet.InMemory) | In-process loopback (no sockets) — fast tests / co-hosting |

**Messaging & security**

| Package | What it does |
|---|---|
| [SetNet.Rpc](src/messaging/SetNet.Rpc/README.md) · [📦](https://www.nuget.org/packages/SetNet.Rpc) | Request/response: `client.CallAsync<TReq,TResp>()` |
| [SetNet.Streams](src/messaging/SetNet.Streams/README.md) · [📦](https://www.nuget.org/packages/SetNet.Streams) | Large-payload streaming: offer/accept, chunks, progress, resume |
| [SetNet.Auth](src/security/SetNet.Auth/README.md) · [📦](https://www.nuget.org/packages/SetNet.Auth) | Enforced auth gate + sessions + reconnect-resume |
| [SetNet.Auth.Jwt](src/security/SetNet.Auth.Jwt/README.md) · [📦](https://www.nuget.org/packages/SetNet.Auth.Jwt) | JWT `IAuthenticator` |
| [SetNet.Auth.OAuth](src/security/SetNet.Auth.OAuth/README.md) · [📦](https://www.nuget.org/packages/SetNet.Auth.OAuth) | OpenID Connect authenticator (auto-refreshed JWKS) |
| [SetNet.BanList](src/security/SetNet.BanList/README.md) · [📦](https://www.nuget.org/packages/SetNet.BanList) | Ban gate (IP/account) + instant kick, pluggable store |
| [SetNet.DdosGuard](src/security/SetNet.DdosGuard/README.md) · [📦](https://www.nuget.org/packages/SetNet.DdosGuard) | Per-IP connection-flood detection + timed auto-ban |
| [SetNet.GeoBlock](src/security/SetNet.GeoBlock/README.md) · [📦](https://www.nuget.org/packages/SetNet.GeoBlock) | Reject connections by country (blocklist/allowlist), pluggable GeoIP |
| [SetNet.ProofOfWork](src/security/SetNet.ProofOfWork/README.md) · [📦](https://www.nuget.org/packages/SetNet.ProofOfWork) | Hashcash admission gate — make bot/flood connections CPU-costly |

**Net / QoS**

| Package | What it does |
|---|---|
| [SetNet.RateLimit](src/net/SetNet.RateLimit/README.md) · [📦](https://www.nuget.org/packages/SetNet.RateLimit) | Per-peer token-bucket inbound gate |
| [SetNet.Fragmentation](src/net/SetNet.Fragmentation/README.md) · [📦](https://www.nuget.org/packages/SetNet.Fragmentation) | Split/reassemble oversize UDP messages |
| [SetNet.Priority](src/net/SetNet.Priority/README.md) · [📦](https://www.nuget.org/packages/SetNet.Priority) | Priority send queue with a per-flush byte budget |
| [SetNet.Congestion](src/net/SetNet.Congestion/README.md) · [📦](https://www.nuget.org/packages/SetNet.Congestion) | AIMD send-rate controller |
| [SetNet.Relay](src/net/SetNet.Relay/README.md) · [📦](https://www.nuget.org/packages/SetNet.Relay) | TURN-style relay hub (opaque forwarding, NAT fallback) |
| [SetNet.NatPunch](src/net/SetNet.NatPunch/README.md) · [📦](https://www.nuget.org/packages/SetNet.NatPunch) | UDP hole-punching: coordinator + simultaneous-probe puncher |
| [SetNet.Upnp](src/net/SetNet.Upnp/README.md) · [📦](https://www.nuget.org/packages/SetNet.Upnp) | UPnP IGD port mapping (SSDP discovery, add/delete mappings) |
| [SetNet.Multiplex](src/net/SetNet.Multiplex/README.md) · [📦](https://www.nuget.org/packages/SetNet.Multiplex) | Logical channels — per-channel ordered dispatch lanes (HOL fix) |

**Realtime & games**

| Package | What it does |
|---|---|
| [SetNet.Rooms](src/realtime/SetNet.Rooms/README.md) · [📦](https://www.nuget.org/packages/SetNet.Rooms) | Rooms/lobbies by code, typed broadcast, join/leave events |
| [SetNet.Rooms.HostMigration](src/realtime/SetNet.Rooms.HostMigration/README.md) · [📦](https://www.nuget.org/packages/SetNet.Rooms.HostMigration) | Host designation + migration on host drop |
| [SetNet.Matchmaking](src/realtime/SetNet.Matchmaking/README.md) · [📦](https://www.nuget.org/packages/SetNet.Matchmaking) | FIFO/skill matchmaking → creates a room to join |
| [SetNet.Party](src/realtime/SetNet.Party/README.md) · [📦](https://www.nuget.org/packages/SetNet.Party) | Parties: leader + ready, queue together |
| [SetNet.Chat](src/realtime/SetNet.Chat/README.md) · [📦](https://www.nuget.org/packages/SetNet.Chat) | Channel text chat + moderation |
| [SetNet.Voice](src/realtime/SetNet.Voice/README.md) · [📦](https://www.nuget.org/packages/SetNet.Voice) | Codec-agnostic voice-chat relay (channels, opaque frames) |
| [SetNet.Lockstep](src/realtime/SetNet.Lockstep/README.md) · [📦](https://www.nuget.org/packages/SetNet.Lockstep) | Deterministic input-synchronous turn engine (RTS) |
| [SetNet.Inventory](src/realtime/SetNet.Inventory/README.md) · [📦](https://www.nuget.org/packages/SetNet.Inventory) | Server-authoritative stackable inventory, atomic grant/revoke |
| [SetNet.Trade](src/realtime/SetNet.Trade/README.md) · [📦](https://www.nuget.org/packages/SetNet.Trade) | Escrow player trading (two-phase confirm, atomic swap) |
| [SetNet.Mail](src/realtime/SetNet.Mail/README.md) · [📦](https://www.nuget.org/packages/SetNet.Mail) | Offline mail with escrowed item attachments |
| [SetNet.Zones](src/realtime/SetNet.Zones/README.md) · [📦](https://www.nuget.org/packages/SetNet.Zones) | Seamless player handoff between world nodes |
| [SetNet.Wallet](src/realtime/SetNet.Wallet/README.md) · [📦](https://www.nuget.org/packages/SetNet.Wallet) | Server-authoritative currencies, atomic deposit/withdraw/transfer |
| [SetNet.Vendor](src/realtime/SetNet.Vendor/README.md) · [📦](https://www.nuget.org/packages/SetNet.Vendor) | NPC shops (buy/sell, stock) via Wallet + Inventory |
| [SetNet.Auction](src/realtime/SetNet.Auction/README.md) · [📦](https://www.nuget.org/packages/SetNet.Auction) | Player auction house (escrow, bids, timer settlement) |
| [SetNet.Crafting](src/realtime/SetNet.Crafting/README.md) · [📦](https://www.nuget.org/packages/SetNet.Crafting) | Recipes crafted atomically through Inventory |
| [SetNet.Loot](src/realtime/SetNet.Loot/README.md) · [📦](https://www.nuget.org/packages/SetNet.Loot) | Weighted server-side drop tables |
| [SetNet.Quests](src/realtime/SetNet.Quests/README.md) · [📦](https://www.nuget.org/packages/SetNet.Quests) | Quests: objectives, progress, rewards |
| [SetNet.Progression](src/realtime/SetNet.Progression/README.md) · [📦](https://www.nuget.org/packages/SetNet.Progression) | Levels/XP with a configurable curve |
| [SetNet.Guilds](src/realtime/SetNet.Guilds/README.md) · [📦](https://www.nuget.org/packages/SetNet.Guilds) | Guilds/clans: roles + shared bank |
| [SetNet.Marketplace](src/realtime/SetNet.Marketplace/README.md) · [📦](https://www.nuget.org/packages/SetNet.Marketplace) | Continuous order-book marketplace (limit orders, matching) |
| [SetNet.StatusEffects](src/realtime/SetNet.StatusEffects/README.md) · [📦](https://www.nuget.org/packages/SetNet.StatusEffects) | Server-authoritative buffs/debuffs (timed, stacking) |
| [SetNet.StateSync](src/realtime/SetNet.StateSync/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync) | Server-authoritative entity replication (snapshots, interpolation) |
| [SetNet.StateSync.SpatialGrid](src/realtime/SetNet.StateSync.SpatialGrid/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.SpatialGrid) | Grid-based interest management |
| [SetNet.StateSync.LagCompensation](src/realtime/SetNet.StateSync.LagCompensation/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.LagCompensation) | Server rewind for fair hit detection |
| [SetNet.StateSync.Prediction](src/realtime/SetNet.StateSync.Prediction/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.Prediction) | Client-side prediction & reconciliation |
| [SetNet.StateSync.NetworkVariable](src/realtime/SetNet.StateSync.NetworkVariable/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.NetworkVariable) | Typed, change-tracked variables |
| [SetNet.StateSync.Rpc](src/realtime/SetNet.StateSync.Rpc/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.Rpc) | Entity-scoped RPCs (typed `On<T>`) |

**Infrastructure**

| Package | What it does |
|---|---|
| [SetNet.DependencyInjection](src/infra/SetNet.DependencyInjection/README.md) · [📦](https://www.nuget.org/packages/SetNet.DependencyInjection) | Construct handlers via `IServiceProvider` |
| [SetNet.Hosting](src/infra/SetNet.Hosting/README.md) · [📦](https://www.nuget.org/packages/SetNet.Hosting) | Run as an `IHostedService` (Generic Host / ASP.NET Core) |
| [SetNet.HealthChecks](src/infra/SetNet.HealthChecks/README.md) · [📦](https://www.nuget.org/packages/SetNet.HealthChecks) | `IHealthCheck` for liveness + connection count |
| [SetNet.Inspector](src/infra/SetNet.Inspector/README.md) · [📦](https://www.nuget.org/packages/SetNet.Inspector) | Built-in HttpListener metrics dashboard |
| [SetNet.Gateway](src/infra/SetNet.Gateway/README.md) · [📦](https://www.nuget.org/packages/SetNet.Gateway) | Raw-relay reverse proxy / player sharding |
| [SetNet.Cluster](src/infra/SetNet.Cluster/README.md) · [📦](https://www.nuget.org/packages/SetNet.Cluster) | Server-to-server broadcast bus (mesh of nodes) |
| [SetNet.Redis](src/infra/SetNet.Redis/README.md) · [📦](https://www.nuget.org/packages/SetNet.Redis) | Redis backplane: shared sessions/bans/room codes across nodes |
| [SetNet.Sharding](src/infra/SetNet.Sharding/README.md) · [📦](https://www.nuget.org/packages/SetNet.Sharding) | Consistent-hash key→node routing + cluster-wide shard directory |
| [SetNet.LoadBalancer](src/infra/SetNet.LoadBalancer/README.md) · [📦](https://www.nuget.org/packages/SetNet.LoadBalancer) | Least-loaded node selection for new sessions |

**Logging & engine bindings**

| Package | What it does |
|---|---|
| [SetNet.Logging.Serilog](src/logging/SetNet.Logging.Serilog/README.md) · [📦](https://www.nuget.org/packages/SetNet.Logging.Serilog) | Route `ILogger` into Serilog |
| [SetNet.Logging.NLog](src/logging/SetNet.Logging.NLog/README.md) · [📦](https://www.nuget.org/packages/SetNet.Logging.NLog) | Route `ILogger` into NLog |
| [SetNet.Logging.ZLogger](src/logging/SetNet.Logging.ZLogger/README.md) · [📦](https://www.nuget.org/packages/SetNet.Logging.ZLogger) | Route `ILogger` into ZLogger (zero-alloc) |
| [SetNet.Unity](src/engine/SetNet.Unity/README.md) · [📦](https://www.nuget.org/packages/SetNet.Unity) | Unity main-thread dispatcher |
| [SetNet.StateSync.Unity](src/engine/SetNet.StateSync.Unity/README.md) | Unity replication components (**UPM source**, not NuGet) |
| [SetNet.Godot](src/engine/SetNet.Godot/README.md) · [📦](https://www.nuget.org/packages/SetNet.Godot) | Godot 4 (C#) main-thread dispatcher + math conversions |
| [SetNet.StateSync.Godot](src/engine/SetNet.StateSync.Godot/README.md) · [📦](https://www.nuget.org/packages/SetNet.StateSync.Godot) | Godot 4 replication components |

## Install

Requires **.NET Standard 2.1** (consumable from .NET Core 3.0+/.NET 5–8, Unity, Mono, MAUI — not .NET Framework).

```bash
dotnet add package SetNet
# the core bundles no serializer — add one (or supply your own ISerializer):
dotnet add package SetNet.MessagePack
```

Then register the serializer once at startup, before connecting:

```csharp
SetNetSerializer.Use(new MessagePackNetSerializer());   // from SetNet.MessagePack
```

Packages ship to **NuGet.org** (public, no auth) and to **GitHub Packages**. To use the GitHub Packages feed, copy [`docs/nuget.config.example`](docs/nuget.config.example) to your solution as `nuget.config` and set `GITHUB_PACKAGES_PAT` (a token with `read:packages`) — see [docs/README.md](docs/README.md#installing-from-github-packages).

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
SetNetSerializer.Use(new MessagePackNetSerializer());   // once, at startup
```

**Or your own format** (JSON, Protobuf, MemoryPack, …) — implement `ISerializer`:

```csharp
public sealed class JsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => System.Text.Json.JsonSerializer.Deserialize<T>(data)!;
}

SetNetSerializer.Use(new JsonSerializer());             // once, at startup
```

Handlers are **strongly typed** — they receive the deserialized message directly (`IServerMessageHandler<ChatMessage>` → `HandleAsync(peer, ChatMessage msg)`); the library serializes on send and deserializes on receive through this one registered serializer. Both ends of a connection must use the same serializer. (Until one is registered, send/receive throws a clear "configure a serializer" error.)

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

In-process benchmark (`dotnet run -c Release --project tests/SetNet.Tests -- bench`, ServerGC):

| Mode | Throughput (1 connection) | Optimized for |
|---|---|---|
| Batched (`SendBatching = true`) | **~1.6M msgs/sec** | throughput |
| Default (`TcpNoDelay = true`) | ~230k msgs/sec | latency |

~10 KB per endpoint; 2,000 connections established in ~110 ms. The default favors latency (every small message sent immediately); enable `SendBatching` for high message rates. These numbers include serialization cost — the library deserializes each inbound message into the handler's typed `T`. Full model, scaling limits and roadmap: [docs/PERFORMANCE.en.md](docs/PERFORMANCE.en.md).

## Documentation

- 📚 **[Documentation home (docs/README.md)](docs/README.md)** — the docs hub: a "which package do I need?" guide, the serialization/payloads explainer, links to every module's README, and everything below.
- 📖 **[User guide (docs/GUIDE.en.md)](docs/GUIDE.en.md)** — full usage manual: handlers, transports, reliable channels, reconnect, batching, hardening, the complete `Configuration` reference, and a production checklist. *(Українською: [docs/GUIDE.ua.md](docs/GUIDE.ua.md))*
- ⚙️ **[Performance (docs/PERFORMANCE.en.md)](docs/PERFORMANCE.en.md)** — performance model, scaling limits, structural roadmap. *(Українською: [docs/PERFORMANCE.ua.md](docs/PERFORMANCE.ua.md))*
- 🧩 **[docs/MODULES.md](docs/MODULES.md)** — the full companion-package catalog (grouped) + the core extension points.
- 📡 **[Communication model (docs/COMMUNICATION.md)](docs/COMMUNICATION.md)** — every send/receive method for client and server: core `SendAsync`, the unified `SetNet.Protocol` (request/reply, fire-and-forget, push; `[Op]`/`[Event]`), and the modules (RPC, StateSync, Streams, Voice, …) — with a full worked example (authoritative item drop broadcast to a room).
- 🏗️ **[CLAUDE.md](CLAUDE.md)** / **[AGENTS.md](AGENTS.md)** — architecture overview for contributors and coding agents.

## Build & test

```bash
dotnet build                                              # build (library targets netstandard2.1)
dotnet test tests/SetNet.UnitTests/SetNet.UnitTests.csproj      # 129 unit + integration tests
dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>   # in-process transport scenarios
dotnet run --project tests/SetNet.Tests -- bench                # throughput / connection benchmark

# chat example (two terminals) — SetNet.Protocol channel + [Op]/[Event]
dotnet run --project examples/Chat/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat/Chat.Client -- 127.0.0.1 5000 alice

# rooms lobby example — typed On<T> broadcast (three terminals: server + two clients)
dotnet run --project examples/Rooms/Rooms.Server -- 127.0.0.1 5001
dotnet run --project examples/Rooms/Rooms.Client -- 127.0.0.1 5001 create      # prints a room code
dotnet run --project examples/Rooms/Rooms.Client -- 127.0.0.1 5001 <ROOMCODE>

# state replication example — server bounces balls, client prints their positions (two terminals)
dotnet run --project examples/StateSync/StateSync.Server -- 127.0.0.1 5002 8
dotnet run --project examples/StateSync/StateSync.Client -- 127.0.0.1 5002
```

Each example now lives in its own folder under `examples/` (Shared/Server/Client). **Full catalog with run commands: [examples/README.md](examples/README.md)** — Chat, Rooms, StateSync, Rpc, Economy, Matchmaking, Party, Trade, Auth, FileTransfer, Voice, Presence.

## Project structure

Projects live under `src/`, grouped by purpose; tests under `tests/`. The core is one package; everything else is an optional companion you pull in only if you need it. **Full catalog: [docs/MODULES.md](docs/MODULES.md).**

```
src/
  core/         SetNet ...................... core library (transport, reliability, hardening)
  serializers/  SetNet.MessagePack .......... recommended serializer (hardened)
                SetNet.Json ................. System.Text.Json (readable, web-friendly)
                SetNet.MemoryPack ........... fast, AOT/IL2CPP-friendly
                SetNet.Protobuf ............. compact, cross-language
                SetNet.Compression .......... transparent Brotli decorator
  transports/   SetNet.WebSockets ........... ws:// transport (config.UseWebSockets())
                SetNet.InMemory ............. in-process loopback (config.UseInMemory())
  messaging/    SetNet.Rpc .................. request/response RPC (client.CallAsync<TReq,TResp>)
  security/     SetNet.Auth ................. auth + sessions (enforced gate, reconnect-resume)
                SetNet.Auth.Jwt ............. JWT authenticator
                SetNet.Auth.OAuth ........... OpenID Connect authenticator (JWKS)
                SetNet.BanList .............. ban gate (IP/account) + kick
                SetNet.DdosGuard ............ connection-flood auto-ban
                SetNet.GeoBlock ............. reject by country (pluggable GeoIP)
                SetNet.ProofOfWork .......... hashcash admission gate (anti-bot)
  net/          SetNet.RateLimit ............ per-peer token-bucket gate
                SetNet.Fragmentation ........ oversize-UDP split/reassemble
                SetNet.Priority ............. priority send queue
                SetNet.Congestion ........... AIMD rate control
                SetNet.Relay ................ TURN-style relay hub (NAT fallback)
  realtime/     SetNet.Rooms ................ rooms/lobbies (join-by-code, broadcast)
                SetNet.Rooms.HostMigration .. host designation + migration
                SetNet.Matchmaking .......... FIFO/skill matchmaking on top of Rooms
                SetNet.Party ................ party/group with leader + ready
                SetNet.Chat ................. channel text chat + moderation
                SetNet.Voice ................ codec-agnostic voice relay
                SetNet.Lockstep ............. deterministic turn engine
                SetNet.StateSync ............ entity replication (snapshots, interpolation)
                SetNet.StateSync.SpatialGrid  grid interest management
                SetNet.StateSync.LagCompensation  server rewind for hit detection
                SetNet.StateSync.Prediction  client rewind & replay
                SetNet.StateSync.NetworkVariable  typed change-tracked variables
                SetNet.StateSync.Rpc ........ entity-scoped RPCs
  infra/        SetNet.DependencyInjection .. handler construction via IServiceProvider
                SetNet.Hosting .............. run as an IHostedService
                SetNet.HealthChecks ......... IHealthCheck for liveness/connections
                SetNet.Inspector ............ HttpListener metrics dashboard
                SetNet.Gateway .............. raw-relay reverse proxy / sharding
                SetNet.Cluster .............. server-to-server broadcast bus (mesh)
                SetNet.Redis ................ Redis backplane (shared sessions/bans/rooms)
  logging/      SetNet.Logging.Serilog ...... Serilog ILogger adapter
                SetNet.Logging.NLog ......... NLog ILogger adapter
                SetNet.Logging.ZLogger ...... ZLogger ILogger adapter (zero-alloc)
  engine/       SetNet.Unity ................ Unity main-thread dispatcher
                SetNet.StateSync.Unity ...... Unity replication components (UPM source)
                SetNet.Godot ................ Godot 4 (C#) dispatcher + math
                SetNet.StateSync.Godot ...... Godot 4 replication components
tests/
  SetNet.UnitTests .................... xUnit unit + integration tests
  SetNet.Tests ....................... in-process scenario harness + benchmark
examples/ ............................ runnable samples: Chat, Rooms (typed broadcast), StateSync (balls)
docs/ ................................ MODULES.md, GUIDE.{en,ua}.md, PERFORMANCE.{en,ua}.md
```

## Status

SetNet has been through extensive adversarial auditing (multi-round correctness convergence + a performance pass) with a full unit/integration suite and in-process scenarios. It is well-suited as the **network layer for .NET ↔ .NET real-time systems** (multiplayer games, chat, collaborative apps).

It is **not** a general-purpose HTTP framework. Everything beyond the raw connection is an opt-in companion package (see [Modules](#modules)) — request/response RPC, auth, rooms/matchmaking/party, state replication, alternative transports (WebSockets/in-memory), QoS, DI/hosting/health-checks, and Unity/Godot bindings. Before production, enable authentication, set the hardening config, and run a soak/load test under realistic traffic.

## License

MIT — see [LICENSE](LICENSE).
