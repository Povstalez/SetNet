# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SetNet is a .NET networking library for client-server communication over **TCP, UDP, or both at once**. It provides a framework for building networked applications with automatic message handler registration, pluggable serialization (no serializer is bundled — MessagePack is available via the **SetNet.MessagePack** companion package, or supply your own `ISerializer`), and utilities for task scheduling.

The transport is pluggable behind a thin abstraction in `SetNet/Core/Transport/`: `ITransportConnection` (a framed message channel to one peer), `ITransportConnector` (client dialer), and `ITransportListener` (server acceptor). `BaseSocket`/`BaseClient`/`BasePeer`/`BaseServer` are transport-agnostic; everything above the transport (`MessageProcessor`, the command executors, handler interfaces, pluggable serialization, heartbeat, lifecycle hooks) is shared by all transports. Select the transport with `Configuration.TransportType` (`Tcp` | `Udp` | `Both`, default `Tcp`).

## Build and Test Commands

**Build the project:**
```bash
dotnet build
```

**Run the unit + integration tests (xUnit):**
```bash
dotnet test tests/SetNet.UnitTests/SetNet.UnitTests.csproj
```

**Run the in-process transport scenarios (manual harness):**
```bash
dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
```

**Run the example chat (separate server + client):**
```bash
dotnet run --project examples/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 alice
```

**Build in release mode:**
```bash
dotnet build -c Release
```

**Clean build artifacts:**
```bash
dotnet clean
```

## Architecture Overview

The framework is organized into several key layers:

### 1. **Core Networking Layer** (`SetNet/Core/`)

- **BaseSocket**: Foundation class providing message-processor integration. Holds an `ITransportConnection` (the transport-agnostic channel) and routes received frames to handlers via `HandleMessage`.

- **Transport abstraction** (`SetNet/Core/Transport/`): `ITransportConnection`/`ITransportConnector`/`ITransportListener` plus implementations in `Tcp/`, `Udp/`, and `Both/`. TCP wraps `NetworkStream` + length-prefix reassembly (reuses `PacketBuilder`). UDP wraps a datagram socket with an emulated-connection handshake, heartbeat, peer demux by endpoint, and an optional `ReliabilityChannel` (sequence/ACK/retransmit/ordered). `Both` is a composite that runs TCP and UDP together. `TransportFactory` builds the right pieces from `Configuration`.
  
- **BaseClient**: Abstract client implementation that connects to a server, handles incoming messages, and manages the connection lifecycle. Subclasses implement `OnConnected()`, `OnDisconnected()`, and `OnError()` hooks.
  
- **BaseServer**: Abstract server implementation that listens for incoming connections and delegates client handling to abstract peer objects. Manages a pool of connected clients.
  
- **BasePeer**: Abstract server-side peer representing a connected client. Handles incoming data from that specific client and sends responses back. Manages bidirectional communication.

The flow: Server accepts connection → creates a BasePeer → peer receives messages → messages routed to handlers → handlers process and respond.

### 2. **Message Handling Framework** (`SetNet/Core/Commands/` + `SetNet/Data/`)

- **ServerCommandExecutor / ClientCommandExecutor**: Use reflection to auto-discover and register message handlers at startup. Look for classes implementing `IServerMessageHandler<T>` or `IClientMessageHandler<T>` decorated with `MessageHandlerAttribute`, instantiate each, and wrap it in an internal invoker (`ServerHandlerInvoker<T>`/`ClientHandlerInvoker<T>`, in `HandlerInvoker.cs`) that deserializes the payload via `SetNetSerializer` and calls the typed handler. Discovery is cached per handler interface in `HandlerDiscovery`.
  
- **MessageHandlerAttribute**: Marks a handler class and specifies its message type (ushort). Used by the executors for reflection-based registration.
  
- **IServerMessageHandler\<TMessage\>**: Interface for handlers that process messages on the server side. Signature: `Task HandleAsync(BasePeer peer, TMessage message)` — the library deserializes the payload into `TMessage` before calling.
  
- **IClientMessageHandler\<TMessage\>**: Interface for handlers that process messages on the client side. Signature: `Task HandleAsync(TMessage message)`.

Message handlers are **strongly typed** (no manual deserialization) and are discovered and instantiated automatically via reflection when the executor is constructed.

- **Raw frame escape hatch**: `BaseSocket.OnRawFrame(ushort type, byte[] data)` (virtual, default no-op) is called for every application frame (system types excluded via `SystemMessageTypes.IsSystem`) before typed dispatch; returning `true` consumes the frame and skips typed handling. Paired with `BaseClient`/`BasePeer.SendRawAsync(type, payload, delivery?)` which sends already-serialized bytes without re-serializing. Together they enable relay/proxy peers that forward traffic with zero (de)serialization while normal handlers stay typed. `SendAsync`/`SendRawAsync` are **public** on `BaseClient`/`BasePeer` (not just protected) so companion packages/plugins can send without subclassing.

- **RPC (optional, separate package `SetNet.Rpc`; depends only on `SetNet`, serializer-agnostic)**: request/response by **composition**, no base class. `await client.CallAsync<TReq,TResp>(methodId, req)` (extension) + `[RpcMethod]` `IRpcHandler<TReq,TResp>` server handlers. The envelope is hand-framed as a `byte[]` (so it rides over any serializer with no MessagePack dependency); bodies go through `SetNetSerializer`. It ships auto-discovered `[MessageHandler]`s (`IServerMessageHandler<byte[]>`/`IClientMessageHandler<byte[]>`) for reserved envelope type ids (65531/65532), completes calls via a static correlation-id registry (client handlers have no connection ref, but ids are process-unique), and relays server exceptions as `RpcException` / enforces per-call timeouts. Call `RpcRuntime.Enable()` once at startup so the package is loaded before handler discovery.

- **Auth + sessions (optional, separate package `SetNet.Auth`; depends only on `SetNet`, serializer-agnostic)**: authentication by **composition**. `server.UseAuth(IAuthenticator, AuthOptions)` installs an **enforced inbound gate** — until a peer authenticates, its application frames (regular + RPC) are dropped; only the auth handshake (reserved types 65529/65530) passes. `client.UseAuth(tokenProvider)` auto-authenticates on connect and every reconnect (via the core `BaseClient.Connected` event), with reconnect-token session resume (rotating single-use tokens, TTL) and multi-session policy (AllowMultiple/KickExisting/RejectNew). Sessions live in a pluggable `ISessionStore` (default in-process `MemorySessionStore`; supply a Redis/DB store for cross-restart/cluster). Enabled by three small public core hooks added for it (general-purpose): `BaseClient.Connected` event, `BaseServer.InboundAuthorizer` (`Func<BasePeer,ushort,bool>`, consulted per app frame via `BaseSocket.AllowInbound`/`BasePeer` override), and public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`. Call `AuthRuntime.Enable()` once at startup. Use over TLS.

- **Rooms/Lobbies (optional, separate package `SetNet.Rooms`; depends only on `SetNet`, serializer-agnostic)**: rooms by **composition** on a dedicated server (server is the hub — no relay). `server.UseRooms(IRoomStore?)` + `var rooms = client.UseRooms()` → `CreateAsync`/`JoinAsync(code)`/`LeaveAsync`/`BroadcastAsync<T>` + `PlayerJoined`/`PlayerLeft`/`MessageReceived`/`Closed` events. Reserved types 65526/65527/65528; hand-framed byte[] protocol; pluggable `IRoomStore` (default `MemoryRoomStore`, 6-char join codes). Uses the core `BaseServer.PeerDisconnected` event (added for it, general-purpose — fires once per peer on removal) to auto-remove a dropped peer from its room and notify others. Server-push events route via a static registry filtered by room code (correct for the usual one-client-per-process; co-located clients share routing). Call `RoomsRuntime.Enable()` at startup.

- **WebSocket transport (optional, separate package `SetNet.WebSockets`; depends only on `SetNet`)**: runs SetNet over `ws://` instead of raw TCP/UDP. `config.UseWebSockets()` sets `TransportType.Custom` + a `WebSocketTransport : ITransportProvider`; everything above the transport (handlers, RPC, rooms, auth) is unchanged. Client via `System.Net.WebSockets.ClientWebSocket`, server via `HttpListener.AcceptWebSocketAsync`. Framing is one binary WS message per SetNet frame: `[2-byte type LE][payload]` (WS message boundaries replace TCP's length prefix). Reliable/ordered like TCP, so `DeliveryMethod` is ignored. `wss://` via a TLS-terminating reverse proxy.

- **In-memory transport (optional, separate package `SetNet.InMemory`; depends only on `SetNet`)**: an in-process loopback transport via the same custom-transport hook. `config.UseInMemory()` sets `TransportType.Custom` + `InMemoryTransport : ITransportProvider`; a client and server sharing the same `Host:Port` key connect entirely in memory (no sockets). Two linked `InMemoryConnection`s exchange whole `TransportMessage`s through a `ConcurrentQueue`+`SemaphoreSlim` async channel (`AsyncChannel<T>`); payloads are copied on send. Reliable/ordered like TCP. A process-wide `InMemoryHub` maps `Host:Port` → listener; the server must `Start()` (registers the listener) before the client connects, else connect throws. Ideal for fast, deterministic integration tests (used by the WS/matchmaking tests) and co-hosting.

- **Matchmaking (optional, separate package `SetNet.Matchmaking`; depends on `SetNet` + `SetNet.Rooms`)**: queue-based matchmaking by **composition** on top of Rooms. `server.UseMatchmaking(IRoomStore store, MatchmakingOptions)` — pass the **same** store as `UseRooms(store)` — plus `var mm = client.UseMatchmaking()` → `FindMatchAsync(MatchRequest)`/`FindAndJoinAsync(req, rooms)`/`CancelAsync()` + `MatchFound` event. A background `Timer` (`TickIntervalMs`) forms matches per queue: FIFO by default, or skill-based (`UseSkill`) grouping players whose skill spread fits inside every member's **acceptance window**, which widens by `SkillWindowGrowthPerSecond` the longer they wait (monotonic timing via `Stopwatch.GetTimestamp()`). On a match it creates a room in the shared store and pushes a targeted `MatchFound{roomCode}` to each matched peer; clients then `rooms.JoinAsync(code)`. Reserved types 65523/65524/65525; hand-framed byte[] protocol; uses `BaseServer.PeerDisconnected` to drop leavers from the queue. Node-local (like Rooms). Call `MatchmakingRuntime.Enable()` (and `RoomsRuntime.Enable()`) at startup.

- **Per-peer rate limiting (optional, separate package `SetNet.RateLimit`; depends only on `SetNet`)**: `server.UseRateLimit(new RateLimitOptions { PerPeerPerSecond = 50, Burst = 100 })` installs a token-bucket **inbound gate** via `BaseServer.InboundAuthorizer` (chained with any previous authorizer, so it composes with Auth). Per-peer buckets live in a `ConditionalWeakTable<BasePeer, TokenBucket>` (auto-cleared on GC). Over-budget frames are dropped (return `false` from the gate).

- **Unity helper (optional, separate package `SetNet.Unity`; depends only on `SetNet`)**: `MainThreadDispatcher` (`Post`/`PostAsync`/`Drain`) to marshal background-thread handler callbacks onto Unity's main thread — enqueue in a handler, `Drain()` in `Update()`. IL2CPP/AOT notes in its README; WebGL unsupported (no threads/sockets).

- **Serilog logging (optional, separate package `SetNet.Logging.Serilog`; depends on `SetNet` + `Serilog`)**: `config.Logger = new SerilogLogger(logger?)` routes SetNet's `ILogger.Log(message, LogLevel)` into Serilog (`ILogger` is pluggable via `Configuration.Logger`). Defaults to `global::Serilog.Log.Logger` when no logger is passed. Level map: Debug→Debug, Info→Information, Warning→Warning, Error→Error.

- **NLog / ZLogger logging (optional, separate packages `SetNet.Logging.NLog` / `SetNet.Logging.ZLogger`)**: same `ILogger` seam as Serilog. `config.Logger = new NLogLogger(logger?)` (defaults to `LogManager.GetLogger("SetNet")`); `config.Logger = new ZLoggerLogger(msLogger)` wraps a `Microsoft.Extensions.Logging.ILogger` and calls `ZLog*` (needs a ZLogger provider for the zero-alloc path; the ZLogger csproj sets `LangVersion=latest` for its interpolated-string handlers). Adapters live in namespace `SetNet.Logging` and fully-qualify the external types with `global::` to avoid namespace shadowing.

- **Geo-blocking (optional, separate package `SetNet.GeoBlock`; depends only on `SetNet`)**: `server.UseGeoBlock(IGeoResolver, GeoBlockOptions)` resolves each peer's country from `peer.RemoteEndPoint` on the `PeerConnected` event and kicks (via `CurrentPeerInfo.Disconnect()`) per a blocklist/allowlist `GeoPolicy` (+ `BlockUnknown`). GeoIP DB is app-supplied (`IGeoResolver.CountryOf(IPAddress)`); ships none. Kick-on-connect, not a gate chain — composes with BanList/Auth.

- **Proof-of-work (optional, separate package `SetNet.ProofOfWork`; depends only on `SetNet`)**: hashcash admission gate. `server.UseProofOfWork(difficulty=20)` chains an `InboundAuthorizer` that drops all frames except the solution (65506) until the peer solves; issues a random challenge (65507) on `PeerConnected`, verifies `SHA-256(challenge‖nonce)` leading-zero-bits ≥ difficulty. `client.UseProofOfWork()` auto-solves off the receive thread on connect. `ProofOfWorkRuntime.Enable()`. Standalone admission gate — stacking with another deny-until gate (Auth) can deadlock; RateLimit/BanList chain fine.

- **Voice relay (optional, separate package `SetNet.Voice`; depends only on `SetNet`)**: codec-agnostic voice hub. `server.UseVoice()` fans each `VoiceUp` (65504) frame out to the sender's channel members as `VoiceDown` (65503) tagged with a stable per-peer speaker id; `VoiceControl` (65505) is join/leave. `var voice = client.UseVoice()` → `JoinChannel`/`LeaveChannel`/`SendFrame(ch, byte[])` + `FrameReceived(speakerId, ch, audio)`. Opaque audio bytes (Opus/PCM), **unreliable** delivery, no server-side mixing. Co-located clients share the static dispatch (one-client-per-process typical). `VoiceRuntime.Enable()`.

- **Relay (optional, separate package `SetNet.Relay`; depends only on `SetNet`)**: TURN-style opaque relay hub (reserved types 65498/65499/65500). `server.UseRelay()` + `var relay = client.UseRelay()` → `AllocateAsync(maxPeers)`/`JoinAsync(code)`/`LeaveAsync`/`SendAsync(bytes)`/`SendToAsync(peerId, bytes)` + `Received(fromPeerId, bytes)`/`PeerJoined`/`PeerLeft`/`Closed`. Hand-framed byte[] protocol with correlated Command/Reply + push Event, mirroring Rooms (server sends Reply/Event via `SendAsync` — serializer-wrapped — to match the `IClientMessageHandler<byte[]>` invoker, **not** `SendRawAsync`). Node-local sessions; per-sender peer ids; opaque forwarding for NAT fallback/tunnelling. `RelayRuntime.Enable()`.

- **Redis backplane (optional, separate package `SetNet.Redis`; depends on `SetNet.Auth` + `SetNet.Rooms` + `SetNet.BanList` + `StackExchange.Redis`)**: Redis-backed store implementations so state is shared across nodes / survives restarts. `RedisSessionStore : ISessionStore` (→ `AuthOptions.SessionStore`; TTL via key expiry, token rotation preserved, `SweepAsync` no-op; `Session.LivePeer` not persisted → cross-node `KickExisting` needs a side channel), `RedisBanStore : IBanStore` (sync StackExchange.Redis API; timed bans use key TTL), `RedisRoomStore : IRoomStore` (atomic code reservation via `StringSet When.NotExists`; shares codes/metadata only — members stay node-local). Share one `IConnectionMultiplexer`; `RedisStores.Connect(cfg)` convenience. No wire types.

- **NAT punch-through (optional, separate package `SetNet.NatPunch`; depends only on `SetNet`)**: UDP hole-punching by **composition** (reserved types 65495/65496/65497). `server.UseNatPunch()` runs a coordinator: the host `RegisterAsync(udpPort)` → code; the guest `PunchAsync(code, udpPort)`; the coordinator pushes both sides the counterpart's candidates (public = server-observed IP + reported UDP port, empty when the transport exposes no remote endpoint; private = NIC enumeration) back-to-back so they punch simultaneously. `NatPuncher.TryPunchAsync(localPort, target)` fires magic-tagged probe/ack datagrams at all candidates and returns the opened endpoint (or null → fall back to `SetNet.Relay`; symmetric NATs won't punch). Co-located clients filter events by session code **and role** (a host consumes guest candidates and vice versa). `NatPunchRuntime.Enable()`.

- **UPnP port mapping (optional, separate package `SetNet.Upnp`; depends only on `SetNet`)**: no wire types, no server piece — talks SSDP (M-SEARCH for IGD v1/v2) + SOAP (WANIPConnection:1/:2, WANPPPConnection:1) to the LAN router. `UpnpPortMapper.DiscoverAsync()` → `UpnpDevice` (`GetExternalIpAsync`/`AddPortMappingAsync`/`DeletePortMappingAsync`), plus `config.MapServerPortsAsync()` mapping whatever the `Configuration` listens on (TCP port, UDP port per `TransportType`). Parsing lives in internal `UpnpXml` (unit-tested via `InternalsVisibleTo`); note the Unix gotcha handled there — a relative `controlURL` like `/ctl` parses as an absolute `file://` URI, so absolute control URLs are scheme-checked.

- **Multiplex (optional, separate package `SetNet.Multiplex`; depends only on `SetNet`)**: logical channels over one connection (reserved type 65494; envelope `[1 channel][2 origType LE][payload]`). `SendMuxAsync(channel, type, msg)` on client/peer; the receiving side demuxes into **per-channel FIFO lanes** (one drain task per lane, Interlocked hand-off) and re-injects via `InjectFrame`, so the original typed handlers fire unchanged — ordering preserved within a channel, independent across channels (dispatch head-of-line-blocking fix; complements `UdpReliableChannels`, which separates *retransmission* streams). Client receive side opts in with `client.UseMultiplex()`; the server side demuxes per peer automatically. `MultiplexRuntime.Enable()`.

- **Streams (optional, separate package `SetNet.Streams`; depends only on `SetNet`)**: large-payload transfer both directions (reserved types 65492 data / 65493 control). Offer(name, length) → receiver accepts (event `OfferReceived` with `AcceptAsync(IStreamSink)`/`RejectAsync`, or auto-accept ≤ `MaxAutoAcceptBytes` into a `MemoryStreamSink`; `FileStreamSink` for disk) → sequential contiguity-validated chunks (requires a reliable+ordered path) → Complete handshake. Sender: `client.UseStreams().SendAsync(name, stream, IProgress<double>?, streamId?)` / `server.UseStreams().SendAsync(peer, ...)`; **resume**: the receiver parks interrupted partials for `PartialTtlSeconds`, the Accept reply carries the resume offset, and re-sending with the same `streamId` (from `StreamsException.StreamId`) re-sends only the tail. Sender-side correlation is a global `PendingSends` map keyed by GUID stream id. `StreamsRuntime.Enable()`.

- **Sharding (optional, separate package `SetNet.Sharding`; depends only on `SetNet`)**: consistent-hash key→node routing (reserved types 65490/65491). `ShardRing` (immutable; FNV-1a-64 over NodeId/key, `VirtualNodes` per node, `GetNode`/`GetNodes(key,n)` for replicas) + `server.UseSharding(ShardingOptions{Nodes, SelfNodeId})` → a directory any node answers plus in-process `Locate`/`IsLocal`; `client.UseSharding().LocateAsync(key)` → connect to the owning node (a directory, not a proxy — for transparent forwarding put `SetNet.Gateway` in front). Membership changes via `UpdateNodes` (atomic ring swap, ~1/N keys remap) — broadcasting them (e.g. over `SetNet.Cluster`) is the app's job. `ShardingRuntime.Enable()`.

- **Cluster bus (optional, separate package `SetNet.Cluster`; depends only on `SetNet`)**: server-to-server broadcast mesh, isolated on its own listener/port (reserved type 65501). `new ClusterNode(ClusterNodeOptions{NodeId, ListenPort, Seeds})` runs a dedicated `BaseServer` (do **not** await its `StartAsync` — it's the accept loop) and dials each seed with a self-managed `ClusterLink : BaseClient` that retries the *initial* connect and re-dials on drop (`AutoReconnect=false`, own maintain loop via `OnDisconnected`). `Publish`/`Publish<T>`/`On<T>`/`Received`/`Stop`. Uses `OnRawFrame`/`SendRawAsync` (no handler discovery); best-effort broadcast, no consensus. `ClusterRuntime.Enable()` is a no-op.

- **Custom transport registration (core hook, general-purpose)**: `TransportType.Custom` + `Configuration.CustomTransport : ITransportProvider` (`CreateConnector`/`CreateListener`), resolved in `TransportFactory`. Lets an external package supply the transport without editing core — used by `SetNet.WebSockets` and `SetNet.InMemory`; also the path for future WebRTC/QUIC/Steam transports.

- **State replication (optional, separate package `SetNet.StateSync`; depends only on `SetNet`; Unity binding `SetNet.StateSync.Unity` is a UPM source package)**: server-authoritative entity replication by **composition**. Engine-agnostic core (headless server + any .NET client). `server.UseStateSync(StateSyncOptions)` → `ServerReplication` (`Spawn`/`Despawn`/`AddObserver`/`InputReceived`); `client.UseStateSync()` → `ClientReplication` (`EntitySpawned`/`EntityDespawned` events, `Entities`, `OwnedEntity`, `SendInput`, `Update()` for interpolation). Archetypes are field schemas registered identically on both ends via `ReplicaRegistry` (`ReplicaSchema.Create(id).Field(FieldType,...)`); the framework interpolates float/vector/quaternion fields and snaps the rest, with optional per-field float quantization. A fixed-rate `Timer` tick sends **delta-compressed** snapshots (Quake-3-style tick/baseline history, per-observer ack) over `DeliveryMethod.Unreliable`; **Spawn/Despawn go `Reliable`**. Interest management is pluggable (`IInterestManager`: `AllInterest` default, `DistanceInterest`). An input channel (`SendInput`→`InputReceived`, `LastProcessedInput` echo) provides prediction plumbing (rewind/replay is game-specific). Reserved wire types 65518–65522. Enabled by the general-purpose core hook `BaseServer.PeerConnected` (symmetric with `PeerDisconnected`, fires once per peer after StartReceive) for auto-observe. Call `StateSyncRuntime.Enable()` at startup. **Unity layer** (`SetNet.StateSync.Unity`, UPM/UnityEngine, not NuGet): `NetworkObject` (archetype schema = ordered concat of its `INetworkComponent`s' fields) + `NetworkTransform` (pos/rot/scale, quantize, owner-authoritative) + `NetworkAnimator` (all controller params + triggers-as-pulse-counter + optional layer state) + `NetworkRigidbody` (velocities, kinematic on non-owners) + `NetworkBehaviour` (custom fields, AOT-safe) + `NetworkManager` (prefab registry, spawn/despawn, drives tick/interp, marshals via `SetNet.Unity.MainThreadDispatcher`).

- **Additional companion packages (see [docs/MODULES.md](docs/MODULES.md) for the full catalog)** — all composition, same patterns. Serializers via `ISerializer`: `SetNet.Json`, `SetNet.MemoryPack`, `SetNet.Protobuf`, `SetNet.Compression` (Brotli decorator). Security: `SetNet.Auth.Jwt`/`SetNet.Auth.OAuth` (`IAuthenticator`s), `SetNet.BanList` (ban gate via `InboundAuthorizer` + kick, `IBanStore`), `SetNet.DdosGuard` (per-IP flood auto-ban on `SetNet.BanList`). Net/QoS: `SetNet.Fragmentation` (oversize-UDP split/reassemble; reserved 65517; uses `InjectFrame`), `SetNet.Priority` (`PrioritySender`), `SetNet.Congestion` (AIMD `CongestionController`). Realtime: `SetNet.Rooms.HostMigration` (uses `RoomServer.RoomHooks()`; reserved 65508), `SetNet.Party` (65511/65512/65513), `SetNet.Chat` (65509/65510), `SetNet.Lockstep` (65514/65515), and StateSync sub-modules `SetNet.StateSync.{SpatialGrid,LagCompensation,Prediction,NetworkVariable,Rpc}` (Rpc reserved 65516). Infra: `SetNet.DependencyInjection` (uses `HandlerActivator.Factory`), `SetNet.Hosting` (`IHostedService`), `SetNet.HealthChecks` (`IHealthCheck`), `SetNet.Inspector` (HttpListener `/metrics`+HTML dashboard), `SetNet.Gateway` (raw-relay reverse proxy via `OnRawFrame`/`SendRawAsync`). Engine: `SetNet.Godot` (Godot 4 dispatcher + math, deps `GodotSharp`).

- **Core hooks added for these (all general-purpose, additive)**: `PeerInfo.RemoteEndPoint`+`BasePeer.RemoteEndPoint` (ban/DDoS/inspector/gateway), `BaseSocket.InjectFrame(type,data)` (fragmentation reassembly / relays), `HandlerActivator.Factory` (DI handler construction), `RoomServer.RoomHooks()` (Rooms server-side join/left events for host migration). Reserved wire-type map is now contiguous **65490–65535** (see MODULES.md).

### 3. **Serialization** (`SetNet/Messaging/`)

- **ISerializer / SetNetSerializer**: Pluggable serialization seam. The core bundles **no** serializer; one is registered once at startup via `SetNetSerializer.Use(ISerializer)` (the backing instance is not exposed publicly). The library uses it everywhere through the `SetNetSerializer.Serialize/Deserialize` façade — both the send path and the typed-handler receive path. The MessagePack adapter (`MessagePackNetSerializer`, `UntrustedData`-hardened) lives in the separate **SetNet.MessagePack** project/package. Until a serializer is registered, the façade throws a clear error.
  
- **PacketBuilder**: Encodes messages into the wire protocol (prefixes with length header) and reassembles incoming data into complete packets. Handles frame boundaries.
  
- **MessageProcessor**: Routes incoming byte[] messages to registered async/sync handlers by type identifier.

### 4. **Configuration** (`SetNet/Config/`)

- **Configuration**: Holds connection settings (Host, Port, BufferSize, MaxConnections), reconnection options (AutoReconnect, MaxReconnectAttempts, ReconnectDelayMs), heartbeat options, **transport options** (`TransportType` Tcp/Udp/Both, `DefaultDelivery`, `UdpPort`, UDP handshake/expiry timeouts, the UDP reliability layer settings incl. `UdpReliableChannels`), **TLS** (`UseSsl`, `ServerCertificate`, `SslTargetHost`, `ServerCertificateValidationCallback`), **dispatch/send tuning** (`TcpNoDelay` Nagle toggle default-on, `MaxInFlightMessages` back-pressure, `SequentialDispatch` ordered dispatch, `SendBatching`/`SendBatchFlushMs` coalesced writes, `SendTimeoutMs` per-write deadline), and **production-hardening limits**: `MaxConnectionsLimit`, `MaxUdpPeers`, `MaxMessageSize` (TCP frame cap), `MaxConnectionsPerIpPerSecond` (per-IP rate limit), `MaxInboundQueue` (per-connection inbound-queue cap / OOM protection). `Validate()` is called on connect/start (and cross-checks e.g. reliable-default vs disabled UDP reliability). A `NetworkMetrics` instance (`Metrics`) exposes live counters.

- **Production hardening** (added after a readiness audit, then a two-round fix→re-audit loop): TLS over TCP via `SslStream` (`Core/Transport/Tcp/TcpTls.cs`); a resilient accept loop (`TcpListenerAdapter` skips a bad/garbage/stalled TLS handshake and continues, with a handshake timeout, instead of dying); `MaxConnectionsLimit`/`MaxUdpPeers` caps and `OnNewClient`+`StartReceive` guarded so a bad accept can't kill the loop (the framework also calls the idempotent `StartReceive` itself); `MaxMessageSize` frame cap + a `length < 2` guard (slow-loris/OOM/negative-length protection); per-IP `RateLimiter` (`Core/RateLimiter.cs`, with idle-window eviction) on TCP accept + UDP handshake; back-pressure dispatch gate (`MaxInFlightMessages`, re-armed per connection generation); `SendTimeoutMs` bounds a stuck-peer write; Both-mode UDP bind tokens are TTL-swept (no leak) and the UDP `Disconnect` control packet is token-validated; client/peer teardown fires `OnDisconnected` exactly once; a throwing application logger can't crash the process; `BaseServer.ActiveConnections`; reconnect/heartbeat errors are logged. **Authentication is intentionally left to the application** (validate inside the server's `OnNewClient`/handlers); MessagePack is bumped to a non-vulnerable version. **UDP datagrams have no per-packet auth/encryption** — route confidentiality/integrity-sensitive traffic over TLS-over-TCP (or Both with reliable delivery).

- **NetworkMetrics** (`SetNet/Diagnostics/`): thread-safe counters (messages sent/received, connections accepted/rejected, reliable retransmits/acks, handshakes dropped) plus `Snapshot()` for export.
  
- **PeerInfo**: Wraps a peer's `ITransportConnection` (and an optional secondary `UdpConnection` in Both mode) along with its metadata (ID, config, server reference, command executor).

### 5. **Utilities** (`SetNet/Utils/`)

- **GameLoopScheduler**: Runs repeating tasks at fixed intervals within a game loop. Useful for server tick updates and scheduled operations. Supports background or blocking execution.
  
- **UpdateScheduler**: (if present) Additional scheduling utility for update-driven architectures.

### 6. **Event System** (`SetNet/Events/`)

- **EventManager**: Pub/sub event system for decoupled communication. Can be used to notify other components of game/network events.

## Key Patterns and Usage

### Adding a New Message Handler

1. Create a message type enum in `SetNet.Tests/Data/MessageTypes.cs`:
   ```csharp
   public enum MessageTypes : ushort
   {
       PlayerMove = 1,
       // ...
   }
   ```

2. Create a data class for the message (must be serializable by MessagePack).

3. Implement a handler class decorated with `MessageHandlerAttribute`:

   **Server-side handler:**
   ```csharp
   [MessageHandler((ushort)MessageTypes.PlayerMove)]
   public class PlayerMoveHandler : IServerMessageHandler<PlayerMoveMessage>
   {
       public async Task HandleAsync(BasePeer peer, PlayerMoveMessage message)
       {
           // Process and respond (message is already deserialized)
       }
   }
   ```

   **Client-side handler:**
   ```csharp
   [MessageHandler((ushort)MessageTypes.UpdateState)]
   public class UpdateStateHandler : IClientMessageHandler<StateUpdateMessage>
   {
       public async Task HandleAsync(StateUpdateMessage message)
       {
           // Update client state
       }
   }
   ```

4. Handlers are auto-registered via reflection on initialization.

### Sending Messages

From a client:
```csharp
await SendAsync<MyMessage>((ushort)MessageTypes.MyMessage, new MyMessage { /* ... */ });
```

From a server peer:
```csharp
await SendAsync<MyResponse>((ushort)MessageTypes.MyResponse, new MyResponse { /* ... */ });
```

### Transport Selection (TCP / UDP / Both)

Choose the transport via `Configuration.TransportType`. Existing TCP code is unchanged (default is `Tcp`).

```csharp
var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682,
    TransportType = TransportType.Both,  // Tcp | Udp | Both
    UdpReliabilityEnabled = true,        // enables the reliable UDP channel
    DefaultDelivery = DeliveryMethod.Reliable
};
```

`SendAsync` takes an optional `DeliveryMethod` (the 2-arg overload uses `Configuration.DefaultDelivery`):

```csharp
await SendAsync(type, msg);                              // uses DefaultDelivery
await SendAsync(type, msg, DeliveryMethod.Unreliable);  // explicit channel
```

Routing by `(TransportType, DeliveryMethod)`:

| TransportType | DeliveryMethod | Carried over |
|---|---|---|
| Tcp  | any | TCP |
| Udp  | Reliable | UDP reliability layer (requires `UdpReliabilityEnabled`, else throws) |
| Udp  | Unreliable | UDP raw datagram |
| Both | Reliable | TCP |
| Both | Unreliable | UDP (falls back to TCP until/if the UDP channel attaches) |

Notes:
- **UDP is an emulated connection**: a handshake assigns peer identity and heartbeat detects liveness, so `OnConnected`/`OnDisconnected`/`BasePeer` work the same as TCP.
- **Both mode** connects TCP first, the server hands the client a UDP bind token over TCP (`SystemMessageTypes.UdpBindToken`), and the client's UDP handshake binds to the same server-side peer. If UDP is unavailable, the client degrades gracefully to TCP-only.
- **MTU**: oversize datagrams (> `UdpMaxDatagramPayload`, default 1200B) are rejected; there is no UDP fragmentation.
- **Heartbeat** in Both mode runs over the TCP lifeline; in UDP-only mode pings/pongs are unreliable datagrams.

### Handling Disconnections and Reconnection

**BaseClient** distinguishes between intentional and unexpected disconnects:

- **Intentional disconnect**: When you call `Disconnect()`, only `OnDisconnected()` is called.
- **Unexpected disconnect**: When server drops connection or IO error occurs, `OnUnexpectedDisconnect()` and `OnError()` are called.

Override the appropriate methods in your BaseClient subclass:

```csharp
public class GameClient : BaseClient
{
    protected override void OnDisconnected()
    {
        // Called when connection closes (intentional or after reconnect fails)
    }

    protected override void OnError(string error)
    {
        // Called only on unexpected errors
    }

    protected override void OnUnexpectedDisconnect()
    {
        // Called when server drops connection unexpectedly
    }

    protected override void OnReconnecting(int attempt, int maxAttempts)
    {
        // Called before each reconnect attempt
        Console.WriteLine($"Reconnecting... {attempt}/{maxAttempts}");
    }

    protected override void OnReconnected()
    {
        // Called when reconnect succeeds
        Console.WriteLine("Successfully reconnected!");
    }

    protected override void OnReconnectFailed()
    {
        // Called when all reconnect attempts are exhausted
        Console.WriteLine("Reconnection failed after all attempts");
    }
}
```

**Enable automatic reconnection:**
```csharp
var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682,
    AutoReconnect = true,               // Enable auto-reconnect
    MaxReconnectAttempts = 3,           // Number of attempts
    ReconnectDelayMs = 1000             // Delay between attempts
};

var client = new GameClient(config);
await client.ConnectAsync();
```

When an error occurs (and AutoReconnect is enabled):
1. `OnError()` fires immediately with error details
2. `OnUnexpectedDisconnect()` fires (only if actual error, not graceful server close)
3. `OnReconnecting()` is called N times (with configurable delay)
4. If reconnect succeeds, `OnReconnected()` fires and the receive loop resumes
5. If all attempts fail, `OnReconnectFailed()` then `OnDisconnected()` fire

When server closes gracefully (bytesRead==0):
- Only `OnDisconnected()` fires (no reconnect, not considered unexpected)

**Disconnect flow on client:**

| Event | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| Client calls `Disconnect()` (intentional) | ❌ | ❌ | ✅ | ❌ |
| Network error / Server crash | ✅ | ✅ | ✅ (if reconnect fails) | ✅ (if enabled) |
| Server graceful close (bytesRead==0) | ❌ | ❌ | ✅ | ❌ |

### Server-side: Handling Client Disconnects in BasePeer

**BasePeer** (server-side) also distinguishes between intentional and unexpected client disconnects:

- **Intentional disconnect**: When you call `Close()` on a peer (server-initiated kick), only `OnDisconnected()` is called.
- **Unexpected disconnect**: When a client crashes or network fails, `OnError()` and `OnUnexpectedDisconnect()` are called.

Override methods in your BasePeer subclass:

```csharp
public class GameServerPeer : BasePeer
{
    protected override void OnDisconnected()
    {
        // Called when connection closes (intentional kick, error, or graceful close)
    }

    protected override void OnError(string error)
    {
        // Called only when there's an unexpected error (IO error, socket error, crash)
        Console.WriteLine(error);
    }

    protected override void OnUnexpectedDisconnect()
    {
        // Called when client crashes or network fails (not on graceful close)
        Console.WriteLine("Client unexpectedly disconnected!");
    }
}
```

**Disconnect flow on server:**

| Event | OnError | OnUnexpectedDisconnect | OnDisconnected |
|---|---|---|---|
| Server calls `Close()` (intentional kick) | ❌ | ❌ | ✅ |
| Client crash / IO error / Socket error | ✅ | ✅ | ✅ |
| Client graceful close (bytesRead==0) | ❌ | ❌ | ✅ |

## Project Structure

Projects are organized under `src/` by purpose (`src/core/`, `src/serializers/`, `src/transports/`, `src/messaging/`, `src/security/`, `src/realtime/`, `src/net/`, `src/logging/`, `src/engine/`), tests under `tests/`. The paths below name each project by its folder; its category prefix under `src/` is implied (e.g. core = `src/core/SetNet/`, Rpc = `src/messaging/SetNet.Rpc/`, StateSync = `src/realtime/SetNet.StateSync/`).

- **SetNet/** (`src/core/SetNet/`): Core library
  - `Core/`: BaseSocket, BaseClient, BaseServer, BasePeer, PacketBuilder, SystemMessageTypes, Commands (CommandExecutor)
  - `Core/Transport/`: transport abstraction + enums (`TransportType`, `DeliveryMethod`); `Tcp/`, `Udp/` (handshake, demux, `ReliabilityChannel`), `Both/` implementations; `TransportFactory`
  - `Config/`: Configuration, PeerInfo
  - `Data/`: Handler interfaces, MessageHandlerAttribute
  - `Messaging/`: MessageProcessor, ISerializer + SetNetSerializer (pluggable serialization seam; core bundles no serializer — the MessagePack adapter `MessagePackNetSerializer` is in the separate `SetNet.MessagePack` project)
  - `Events/`: EventManager
  - `Logging/`: ILogger, ConsoleLogger, NoOpLogger
  - `Utils/`: GameLoopScheduler, UpdateScheduler

- **SetNet.Tests/**: Manual in-process scenario harness demonstrating the framework
  - `Core/`: MainServer, MainClient, PlayerPeer, `Scenarios` (in-process transport tests), LossStats
  - `Data/`: MessageTypes, TestMessage, UpdateClientIdMessage, LossCountMessage
  - `Messages/`: Handler implementations for test messages
  - `Program.cs`: scenario dispatcher — `dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>`

- **SetNet.UnitTests/**: xUnit unit + integration test project (`dotnet test`)
  - Unit: PacketBuilder (incl. fragmentation), UdpDatagram, AsyncQueue, MonotonicClock, Configuration.Validate, MessageProcessor, CommandExecutor, ReliabilityChannel (ordered/dedup)
  - `Integration/`: end-to-end TCP/UDP/loss/Both round-trips via a small echo harness. The library exposes internals to this project via `[InternalsVisibleTo]`.

- **examples/**: A runnable chat example using the library, split into separate processes
  - `Chat.Shared/`: message-type enum + MessagePack DTOs shared by both ends
  - `Chat.Server/`: `ChatServer`/`ChatPeer` + server handlers (broadcast, join) + entry point
  - `Chat.Client/`: `ChatClient` + client handlers (render broadcast/system notices) + console UI

## Debugging Tips

- Message handlers are auto-registered via reflection. If a handler isn't being called, verify:
  1. The class implements `IServerMessageHandler<T>` or `IClientMessageHandler<T>`
  2. It's decorated with `MessageHandlerAttribute` with the correct message type
  3. Both the message type (ushort) and the generic `T` match what's being sent
  4. The handler is in an assembly loaded by the AppDomain

- Connection issues often stem from Configuration mismatches (host/port). Verify both client and server use the same values.

- `PacketBuilder` handles incomplete packets across buffer boundaries. If messages seem corrupted, check that the message type and serialization are consistent.
