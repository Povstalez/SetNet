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
dotnet run --project examples/Chat/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat/Chat.Client -- 127.0.0.1 5000 alice
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

- **RPC (optional, separate package `SetNet.Rpc`; depends only on `SetNet`, serializer-agnostic)**: request/response by **composition**, no base class. `await client.CallAsync<TReq,TResp>(methodId, req)` (extension) + `[RpcMethod]` `IRpcHandler<TReq,TResp>` server handlers. It is a **thin alias over the unified protocol**: `client.CallAsync(methodId, req)` == `client.RequestAsync(Channels.Rpc, methodId, req)` (the RPC method id is the op; bodies go through `SetNetSerializer`). Server side, one `[ProtocolChannel(Channels.Rpc)] RpcChannelService : IChannelService` hands each request to the discovered `[RpcMethod] IRpcHandler<TReq,TResp>` via `RpcMethodDispatcher`; a throwing handler / unknown method surfaces as `RpcException`, and per-call timeouts come from `RequestAsync`. No own wire ids (rides the 65447 envelope). Call `RpcRuntime.Enable()` once at startup so its channel service + handlers are discoverable.

- **Unified messaging protocol (`SetNet.Protocol`, in core — `SetNet/Messaging/Protocol/`)**: the shared request/reply + fire-and-forget + server-push primitive that (almost) every command/reply/event companion module now rides, so message-sending is **uniform across modules**. One reserved envelope wire id (`ProtocolTypes.Envelope` = 65447) carries all of it, demultiplexed by `Channels.X` (a central channel-id registry) + a per-module `op` id; client and server keep independent dispatch tables, so a single wire id serves both directions. API — client: `RequestRawAsync(channel, op, body)` / `RequestAsync<TReq,TResp>` (correlated, one shared `ProtocolCorrelation` registry + timeout), `PostRawAsync` / `PostAsync<T>` (fire-and-forget), `On<T>` / `OnRaw(channel, op, …)` (server-push subscription, one shared `ProtocolSubscriptions` registry, returns `IDisposable`) — **or declare push handlers with `[Event((ushort)MyEvt.X)]` methods on a `[ProtocolChannel]` class** (auto-discovered/subscribed by `ClientEventDiscovery` on first event, symmetric to server `[Op]`; the two styles coexist, both fire; use imperative `On<T>` when the handler must close over per-instance state); server: `[ProtocolChannel(Channels.X)] IChannelService.HandleAsync(ChannelRequest)` (auto-discovered like `[MessageHandler]`, resolved lazily by `ChannelServiceRegistry`) with `request.Op`/`request.RawBody`/`request.ReplyRawAsync`, plus `peer.PublishRawAsync(channel, op, body)` / `peers.PublishRawAsync(…)` for push. **For a channel with many ops, skip the `switch`: put `[ProtocolChannel]` on a plain class and annotate one method per op with `[Op((ushort)MyOp.X)]`** — `OpRouter` binds parameters by type (`BasePeer`, `ChannelRequest`, `byte[]` raw body, or one typed body via `SetNetSerializer`) and turns the return value into the reply (`T`/`Task<T>` → serialized, `byte[]`/`Task<byte[]>` → raw, `void`/`Task` → no reply), throwing to fail; a class that implements `IChannelService` keeps manual control and its `[Op]` methods are ignored. Control bodies stay hand-framed `byte[]` (serializer-agnostic); the typed `<T>` overloads use `SetNetSerializer` for the app's own types. A server handler **throws `ProtocolException`** to fail a request; the caller re-throws it and each module maps it back to its own exception type (`RoomException`, `TradeException`, …). This **replaced every migrated module's bespoke correlation registry, event fan-out registry, and Command/Reply/Event wire-type triple**. The two core envelope handlers live in core (always discovered), so a migrated module needs no `[MessageHandler]` of its own and its `Runtime.Enable()` is now only an assembly-load nudge for channel-service discovery. **Migrated (24):** Rooms, Matchmaking, Party, Chat, Inventory, Wallet, Trade, Mail, Crafting, Loot, Progression, Quests, Guilds, Auction, Vendor, Marketplace, StatusEffects, Zones, Relay, NatPunch, Sharding, LoadBalancer, Rooms.HostMigration, Lockstep. `SetNet.Rpc` also rides the protocol now (as a thin `CallAsync`→`RequestAsync` alias on `Channels.Rpc`). **Intentionally NOT migrated (different message shapes — keep their own reserved ids):** `SetNet.StateSync`(+`.Rpc`), `SetNet.Voice`, `SetNet.Fragmentation`, `SetNet.Multiplex`, `SetNet.Streams`, `SetNet.Cluster`, `SetNet.Auth`, `SetNet.ProofOfWork`. *(The per-module “reserved types NNN” notes below predate this consolidation and are superseded by the single envelope id for the migrated modules.)*

- **Auth + sessions (optional, separate package `SetNet.Auth`; depends only on `SetNet`, serializer-agnostic)**: authentication by **composition**. `server.UseAuth(IAuthenticator, AuthOptions)` installs an **enforced inbound gate** — until a peer authenticates, its application frames (regular + RPC) are dropped; only the auth handshake (reserved types 65529/65530) passes. `client.UseAuth(tokenProvider)` auto-authenticates on connect and every reconnect (via the core `BaseClient.Connected` event), with reconnect-token session resume (rotating single-use tokens, TTL) and multi-session policy (AllowMultiple/KickExisting/RejectNew). Sessions live in a pluggable `ISessionStore` (default in-process `MemorySessionStore`; supply a Redis/DB store for cross-restart/cluster). Enabled by three small public core hooks added for it (general-purpose): `BaseClient.Connected` event, `BaseServer.InboundAuthorizer` (`Func<BasePeer,ushort,bool>`, consulted per app frame via `BaseSocket.AllowInbound`/`BasePeer` override), and public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`. Call `AuthRuntime.Enable()` once at startup. Use over TLS.

- **Rooms/Lobbies (optional, separate package `SetNet.Rooms`; depends only on `SetNet`, serializer-agnostic)**: rooms by **composition** on a dedicated server (server is the hub — no relay). `server.UseRooms(IRoomStore?)` + `var rooms = client.UseRooms()` → `CreateAsync`/`JoinAsync(code)`/`LeaveAsync`/`BroadcastAsync<T>` + `PlayerJoined`/`PlayerLeft`/`MessageReceived`/`Closed` events. Rides the unified `SetNet.Protocol` on `Channels.Rooms` (hand-framed byte[] bodies); pluggable `IRoomStore` (default `MemoryRoomStore`, 6-char join codes). Uses the core `BaseServer.PeerDisconnected` event (added for it, general-purpose — fires once per peer on removal) to auto-remove a dropped peer from its room and notify others. **Server-side membership/broadcast helpers** (via the reusable `IPeerGroups` primitive): `server.RoomGroups()` + aliases `RoomCodeOf`/`MembersOfRoom(code)`/`MembersInRoomOf(peer)`/`OthersInRoomOf(peer)` and `BroadcastToRoomAsync`/`BroadcastToRoomOfAsync(peer,…,includeSelf)`/`BroadcastToRoomExceptAsync` — room-scoped fan-out without a hand-maintained map (`RoomServerState` keeps a code→Room index). `SetNet.Party` exposes the same via `PartyGroups()`/`OthersInPartyOf`/`BroadcastToPartyOfAsync`. Server-push events route via a static registry filtered by room code (correct for the usual one-client-per-process; co-located clients share routing). Call `RoomsRuntime.Enable()` at startup.

- **WebSocket transport (optional, separate package `SetNet.WebSockets`; depends only on `SetNet`)**: runs SetNet over `ws://` instead of raw TCP/UDP. `config.UseWebSockets()` sets `TransportType.Custom` + a `WebSocketTransport : ITransportProvider`; everything above the transport (handlers, RPC, rooms, auth) is unchanged. Client via `System.Net.WebSockets.ClientWebSocket`, server via `HttpListener.AcceptWebSocketAsync`. Framing is one binary WS message per SetNet frame: `[2-byte type LE][payload]` (WS message boundaries replace TCP's length prefix). Reliable/ordered like TCP, so `DeliveryMethod` is ignored. `wss://` via a TLS-terminating reverse proxy.

- **In-memory transport (optional, separate package `SetNet.InMemory`; depends only on `SetNet`)**: an in-process loopback transport via the same custom-transport hook. `config.UseInMemory()` sets `TransportType.Custom` + `InMemoryTransport : ITransportProvider`; a client and server sharing the same `Host:Port` key connect entirely in memory (no sockets). Two linked `InMemoryConnection`s exchange whole `TransportMessage`s through a `ConcurrentQueue`+`SemaphoreSlim` async channel (`AsyncChannel<T>`); payloads are copied on send. Reliable/ordered like TCP. A process-wide `InMemoryHub` maps `Host:Port` → listener; the server must `Start()` (registers the listener) before the client connects, else connect throws. Ideal for fast, deterministic integration tests (used by the WS/matchmaking tests) and co-hosting.

- **Matchmaking (optional, separate package `SetNet.Matchmaking`; depends on `SetNet` + `SetNet.Rooms`)**: queue-based matchmaking by **composition** on top of Rooms. `server.UseMatchmaking(IRoomStore store, MatchmakingOptions)` — pass the **same** store as `UseRooms(store)` — plus `var mm = client.UseMatchmaking()` → `FindMatchAsync(MatchRequest)`/`FindAndJoinAsync(req, rooms)`/`CancelAsync()` + `MatchFound` event. A background `Timer` (`TickIntervalMs`) forms matches per queue: FIFO by default, or skill-based (`UseSkill`) grouping players whose skill spread fits inside every member's **acceptance window**, which widens by `SkillWindowGrowthPerSecond` the longer they wait (monotonic timing via `Stopwatch.GetTimestamp()`). On a match it creates a room in the shared store and pushes a targeted `MatchFound{roomCode}` to each matched peer; clients then `rooms.JoinAsync(code)`. Reserved types 65523/65524/65525; hand-framed byte[] protocol; uses `BaseServer.PeerDisconnected` to drop leavers from the queue. Node-local (like Rooms). Call `MatchmakingRuntime.Enable()` (and `RoomsRuntime.Enable()`) at startup.

- **Per-peer rate limiting (optional, separate package `SetNet.RateLimit`; depends only on `SetNet`)**: `server.UseRateLimit(new RateLimitOptions { PerPeerPerSecond = 50, Burst = 100 })` installs a token-bucket **inbound gate** via `BaseServer.InboundAuthorizer` (chained with any previous authorizer, so it composes with Auth). Per-peer buckets live in a `ConditionalWeakTable<BasePeer, TokenBucket>` (auto-cleared on GC). Over-budget frames are dropped (return `false` from the gate).

- **Unity helper (optional, separate package `SetNet.Unity`; depends only on `SetNet`)**: `MainThreadDispatcher` (`Post`/`PostAsync`/`Drain`) to marshal background-thread handler callbacks onto Unity's main thread — enqueue in a handler, `Drain()` in `Update()`. IL2CPP/AOT notes in its README; WebGL unsupported (no threads/sockets).

- **Serilog logging (optional, separate package `SetNet.Logging.Serilog`; depends on `SetNet` + `Serilog`)**: `config.Logger = new SerilogLogger(logger?)` routes SetNet's `ILogger.Log(message, LogLevel)` into Serilog (`ILogger` is pluggable via `Configuration.Logger`). Defaults to `global::Serilog.Log.Logger` when no logger is passed. Level map: Debug→Debug, Info→Information, Warning→Warning, Error→Error.

- **NLog / ZLogger logging (optional, separate packages `SetNet.Logging.NLog` / `SetNet.Logging.ZLogger`)**: same `ILogger` seam as Serilog. `config.Logger = new NLogLogger(logger?)` (defaults to `LogManager.GetLogger("SetNet")`); `config.Logger = new ZLoggerLogger(msLogger)` wraps a `Microsoft.Extensions.Logging.ILogger` and calls `ZLog*` (needs a ZLogger provider for the zero-alloc path; the ZLogger csproj sets `LangVersion=latest` for its interpolated-string handlers). Adapters live in namespace `SetNet.Logging` and fully-qualify the external types with `global::` to avoid namespace shadowing.

- **Geo-blocking (optional, separate package `SetNet.GeoBlock`; depends only on `SetNet`)**: `server.UseGeoBlock(IGeoResolver, GeoBlockOptions)` resolves each peer's country from `peer.RemoteEndPoint` on the `PeerConnected` event and kicks (via `CurrentPeerInfo.Disconnect()`) per a blocklist/allowlist `GeoPolicy` (+ `BlockUnknown`). GeoIP DB is app-supplied (`IGeoResolver.CountryOf(IPAddress)`); ships none. Kick-on-connect, not a gate chain — composes with BanList/Auth.

- **Proof-of-work (optional, separate package `SetNet.ProofOfWork`; depends only on `SetNet`)**: hashcash admission gate. `server.UseProofOfWork(difficulty=20)` chains an `InboundAuthorizer` that drops all frames except the solution (65506) until the peer solves; issues a random challenge (65507) on `PeerConnected`, verifies `SHA-256(challenge‖nonce)` leading-zero-bits ≥ difficulty. `client.UseProofOfWork()` auto-solves off the receive thread on connect. `ProofOfWorkRuntime.Enable()`. Standalone admission gate — stacking with another deny-until gate (Auth) can deadlock; RateLimit/BanList chain fine.

- **Inventory (optional, separate package `SetNet.Inventory`; depends only on `SetNet`)**: server-authoritative stackable player inventory (reserved types 65487/65488/65489). `server.UseInventory(IInventoryStore?, InventoryOptions?)` → `InventoryServer` — game logic calls `GrantAsync`/`TryRevokeAsync`/`GetAsync` **by player key** (online or not); `TryRevokeAsync` is the atomic take-if-enough primitive trades/mail rely on. `client.UseInventory()` → read-only `GetAsync` + `Changed` push. Player key via `InventoryOptions.PlayerKey` (default = connection id — override to the authenticated account id for durable inventories). Online registry keyed off `PeerConnected`/`PeerDisconnected`. Stacks are `(ItemId, Count)` — per-instance data encodes into the id. Pluggable `IInventoryStore` (`MemoryInventoryStore` default). `InventoryRuntime.Enable()`.

- **Trade (optional, separate package `SetNet.Trade`; depends on `SetNet` + `SetNet.Inventory`)**: player-to-player escrow trading (reserved types 65484/65485/65486). Two-phase state machine (Propose → Offer/Ready → both ready ⇒ Confirming → both Confirm ⇒ swap); editing any offer resets ready/confirm so the partner re-approves the exact final offer. `ExecuteSwap` revokes both sides' offers via `InventoryServer.TryRevokeAsync` (compensating re-grant on any shortfall) then cross-grants. `server.UseTrade(inventory)` + `client.UseTrade(selfPlayerKey?)` → propose/offer/ready/confirm/cancel + `TradeRequested`/`Updated`/`Completed`/`Cancelled`. Events are **per-recipient perspective** (`TradeView`), so co-located clients must pass `selfPlayerKey` to route correctly (single-client-per-process leaves it null); a disconnect auto-cancels. `TradeRuntime.Enable()`.

- **Mail (optional, separate package `SetNet.Mail`; depends on `SetNet` + `SetNet.Inventory`)**: offline in-game mail with item attachments (reserved types 65481/65482/65483). `server.UseMail(IMailStore?, InventoryServer? inventory, MailOptions?)` → `MailServer` (+ `SendSystemAsync` for server-originated mail); `client.UseMail()` → send/list/read/claim/delete + `Received` push. Attachments are **escrowed** out of the sender's inventory at send (rolled back on shortfall) and granted to the recipient on claim; deleting unclaimed mail returns items to the sender — items live in exactly one place. Attachments require the inventory hub. Pluggable `IMailStore` (`MemoryMailStore` default); opaque `byte[]` body. `MailRuntime.Enable()`.

- **Zones (optional, separate package `SetNet.Zones`; depends only on `SetNet`)**: seamless player handoff between world nodes (reserved types 65478/65479/65480). `server.UseZones(IHandoffStore?, ZoneOptions?)` → `ZonesServer.TransferAsync(peer, ZoneTarget, carryState)` stashes carried state under a one-time token in the (shared) store and pushes the client a migrate instruction; `client.UseZones()` → `TransferRequested` event + `ClaimAsync(token)` on the destination node retrieves the state. **The `IHandoffStore` must be shared (Redis/DB) across processes** for real cross-node handoff (`MemoryHandoffStore` default = co-located/tests only). Carries state; doesn't pick the node — pair with `SetNet.Sharding` (`ring.GetNode(zoneId)`). One-time TTL'd tokens. `ZonesRuntime.Enable()`.

- **GeoData (optional, separate package `SetNet.GeoData`; depends only on `SetNet`; server-side, no wire protocol)**: the server's knowledge of the scene behind one interface `IGeoData` — `IsWalkable`/`LineOfSight`/`CanWalkStraight`/`Raycast`/`SampleNearestWalkable`/`SampleHeight`/`Bounds`. Three impls: `GridGeoData` (2.5D occupancy+height nav-grid, DDA-sampled queries; `GridGeoDataBuilder`; single-surface, slopes/steps via `MaxStep`), **`LayeredGridGeoData`** (the L2-style **multi-storey grid** — a cell holds several stacked walkable height *layers*, CSR-packed; every query carries the agent Y and resolves to the nearest layer, so floors/bridges/overpasses work **without a nav-mesh**; `TryLayerNear` is the storey-resolution primitive, `SetWall` for full-height walls; LoS occluded by walls + floors strictly between the endpoints; `LayeredGridGeoDataBuilder.AddLayer`), and `NavMeshGeoData` (triangles + adjacency + an XZ spatial-accel grid; Möller-Trumbore raycast, barycentric closest-point). **Multi-storey/overhangs/floors**: use `LayeredGridGeoData` (grid) or the Y-aware `NavMeshGeoData` (`TriangleAt`/`SampleHeight`/`IsWalkable` resolve to the floor nearest the query Y, `CanWalkStraight` bounded by `WalkYTolerance`); plain `GridGeoData` stays single-surface. **Sectored/zoned worlds**: `SectoredGeoData` (+ `SectoredGeoDataBuilder`) stitches N per-sector geodatas (any mix of grid/layered/navmesh, can stack in Y) into one seamless `IGeoData` via a coarse XZ lookup grid — point queries route to the owning sector, segment queries (LoS/CanWalkStraight) split at sector borders and delegate each piece; `GeoDataManifest` (magic "SNGM") is a portable index (`.geomap`: sector id + relative `.geo` path + bounds) with `GeoDataManifest.Load(path)` rebuilding the whole world. Ships its own `Vec3`/`Bounds`/`RaycastHit` math (engine-agnostic). `GeoDataFile.Save/Load` is a portable binary format (magic "SNGD", kind 1=grid/2=navmesh/3=layered-grid). Bake it from Unity via **`SetNet.GeoData.Unity`** (UPM tool, `SetNet > Bake GeoData`: NavMesh / Colliders / **Layered** multi-surface sweep / **Sectors** = tile-the-world + `.geomap`; auto-bounds one-click; **`GeoDataGizmo` Scene-view debug visualizer** in a runtime asmdef reads the baked `.geo`/`.geomap` and draws cells/layers/walls/navmesh/sector outlines — writes the file format directly, no SetNet dep). Foundation for `SetNet.PathFinding` and `SetNet.Mobs`. No runtime, no `xxxRuntime.Enable()`.

- **PathFinding (optional, separate package `SetNet.PathFinding`; depends on `SetNet` + `SetNet.GeoData`; server-side, no wire protocol)**: path search + movement over an `IGeoData`. `Pathfinding.For(geo)` returns the right `IPathfinder`: `GridPathfinder` (A* 8-connected octile, no corner-cut), **`LayeredGridPathfinder`** (multi-storey — nodes are *layers*, steps to the neighbour cell's layer within `MaxStep`, so routes climb stairs/cross bridges between floors), `NavMeshPathfinder` (triangle A* → portal-midpoint corridor → string-pull smoothing via `CanWalkStraight`; a robust stand-in, not a full funnel), or **`SectoredPathfinder`** (for `SectoredGeoData`: within a sector delegates to that sector's native pathfinder exactly; across sectors routes the sector-adjacency graph + stitches per-sector legs through border portals). **Built for MMO scale**: a pathfinder is built **once and reused** (Mobs holds it) — per-query working memory (`SearchState`: g/came/closed + open heap) is **pooled** and **generation-stamped** (a query costs O(nodes expanded), not O(map size); nothing allocated on the warm hot path; pool is thread-safe for parallel AI ticks); `MaxExpansions` caps the worst case. `FindPath(from,to)` → `Path` (`Waypoints`/`Length`/`IsEmpty`; unreachable = empty, not a throw). `PathFollower.Step(current, maxDistance)` walks an entity along a `Path` (`Arrived` flag). `MinHeap` (netstandard2.1 has no `PriorityQueue`). No runtime.

- **NPC (optional, separate package `SetNet.NPC`; depends on `SetNet` + `SetNet.GeoData` for `Vec3`)**: non-living **interactive** entities (vendors, buffers, teleporters, dialogue) with **custom, extensible interaction logic** (reserved-id-free — rides the unified protocol on `Channels.Npc`). Write one `INpcBehaviour` per type; the framework handles registration/spawn/zone-interest/the interact request-reply/range. `server.UseNpc()` → `NpcServer` (`Register(behaviour)`, `Spawn(NpcSpawn)→id`); `client.UseNpc()` → `InteractAsync(npcId, action, payload?)` + `EnterZoneAsync(zone)` (populates `Nearby`) + `NpcSpawned` event. Interaction returns an `NpcResponse` with an optional **capability hand-off** (e.g. `VendorNpcBehaviour("blacksmith")` → `Capability = "vendor:blacksmith"`) so the client then talks to the matching domain module (Vendor/Quests/…). Behaviours resolve collaborators from `NpcOptions.Services` (`IServiceProvider`). Ships `VendorNpcBehaviour`/`BufferNpcBehaviour`/`TeleporterNpcBehaviour`. `NpcRuntime.Enable()`.

- **Mobs (optional, separate package `SetNet.Mobs`; depends on `SetNet` + `SetNet.GeoData` + `SetNet.PathFinding`)**: hostile AI entities that move/attack/chase, **each mob type with its own logic** (reserved-id-free — rides the unified protocol on `Channels.Mobs`). Write one `IMobBrain` per type (or `MobBrain.Compose(...)` from components); the framework runs the server-authoritative tick loop (perception via `MobSenses`/`IMobPerception`, `ThreatTable`, movement over PathFinding, telegraphed `MobAbility` casts, death/loot/respawn) and sleeps a mob when unobserved. Ships four archetype brains: `AggressiveBrain`, `PassiveRetaliateBrain`, `RangedBrain`, `CasterBrain`. **StateSync is OPTIONAL**: replication goes through an `IMobReplication` seam (default `NullMobReplication`) and player positions through a seam (`MobOptions.PlayerPosition`/`AllPlayers` or `IPlayerPositions`), so the whole AI — perception, the interest/sleep gate, combat — runs **headlessly with `mobs.Update(dtMs)`** and no StateSync (or connections) at all. `server.UseMobs(MobOptions)` → `MobServer` (`Register`, `Spawn(MobSpawn)→id`, `Update(dtMs)` when `UseInternalTimer=false`, `Mobs`, `MobMoved`/`MobUpdated` events); `client.UseMobs()` for attack + push events. `MobsRuntime.Enable()`.

- **Mobs StateSync adapter (optional, separate package `SetNet.Mobs.StateSync`; depends on `SetNet.Mobs` + `SetNet.StateSync`)**: the opt-in bridge for the seam above. `StateSyncMobReplication : IMobReplication` maps a mob's spawn/move/despawn onto a `ServerReplication` archetype (`BuildSchema(id)`); wire it with `MobOptions.Replication = world.StateSyncReplication()`. Present ⇒ mobs replicate over StateSync; absent ⇒ Mobs still ticks (the whole point of the seam).

- **Locomotion (optional, separate package `SetNet.Locomotion`; depends on `SetNet.GeoData` + `SetNet.PathFinding`; server-side, replicates NOTHING)**: one **unified server-side movement tick** for everything that moves (players/mobs/NPCs/projectiles). `server.UseLocomotion(geo, LocomotionOptions{Hz})` (or `new LocomotionSystem(geo, opts)`) → `LocomotionSystem`; `CreateMover(start, speed, owner?)` → `Mover` that is **auto-subscribed** (ticking from creation; `Dispose()` un-subscribes). The system advances every mover's `Position` along a pathfound route N Hz (internal timer or `Update(dtMs)`), reusing one pooled `IPathfinder`. `Mover`: `Position`/`Speed`/`Owner`/`Destination`/`IsMoving`/`GoTo(point)` (server-authoritative; false if unreachable)/`Stop`/`Warp`/`DestinationReached`. **It sends nothing** — you read `Position` and replicate your own way; `LocomotionSystem.Started` fires when a mover gets a new destination, the moment to send just the **point** to clients (L2-style). Client re-paths from the point and animates with the Unity `NavAgent` (`SetNet.Locomotion.Unity`, UPM: walks a `List<Vector3>` at `Speed` = replicated move-speed; pure UnityEngine). Uses `SetNet.GeoData.Vec3`. **Mobs can move through it too**: `SetNet.Mobs` exposes an optional movement seam `MobOptions.Mover` (`IMobMover`, default null = built-in path-follower); the bridge package **`SetNet.Mobs.Locomotion`** (`loco.AsMobMover()`) implements it over a `LocomotionSystem` so players + mobs share one movement tick + one `Started` replication hook (mirrors the `IMobReplication`/`Mobs.StateSync` pattern; default behaviour unchanged).

- **Character / RPG systems (gameplay foundation — all server-side unless noted; Stats underpins the rest)**:
  - **`SetNet.Stats`** (deps `SetNet`; server-side, no wire): custom stat vocabulary. `StatSchema.Create().Define(id, base, min, max).Build()` (reused across players + mob types); each entity gets a `StatSet` whose `Get(statId)` computes `clamp((base + Σflat)·(1 + ΣpercentAdd)·Π(1+percentMult))` from `StatModifier`s (`Flat`/`PercentAdd`/`PercentMult`, tagged with a `Source` for `RemoveBySource`), cached + `Changed` event. Foundation for Combat/Abilities/Equipment.
  - **`SetNet.Combat`** (deps `SetNet.Stats`; no wire): `CombatResolver.Resolve(attacker, defender, AttackSpec)` → `DamageResult` via a pluggable `ICombatFormula` (`StandardCombatFormula`: attackPower·coeff, crit, armor mitigation `raw·armorK/(armorK+defense)`, min-damage clamp). Which stats mean attack-power/defense/crit is configurable (`CombatStatKeys`). Ships a `Health` pool; `ResolveAndApply(...)` deals + applies. Seedable RNG for deterministic tests.
  - **`SetNet.Abilities`** (deps `SetNet.Stats` + `SetNet.Combat`; `Channels.Abilities`): abilities with `CooldownMs`/`ResourceCost`/`Range`/`TargetKind` + composable `IAbilityEffect`s (`DamageEffect` via Combat, `HealEffect`, `BuffEffect` = timed stat modifiers). Entity stats/health/positions/resources come through `AbilityOptions` seams (`StatsOf`/`HealthOf`/`PositionOf`/`ResourceOf`), so **players and mobs share the system**. `server.UseAbilities(options).Define(AbilityDefinition)` + `TryUseAsync(casterKey, id, AbilityTarget)` (the one authority path); `client.UseAbilities().UseAsync(id, targetKey?, point?)`. `AbilitiesRuntime.Enable()`.
  - **`SetNet.Equipment`** (deps `SetNet.Stats` + `SetNet.Inventory`; `Channels.Equipment`): **custom slots** via `EquipmentSchema.Create().Slot(id, accepts?)`. `server.UseEquipment(inventory, EquipmentOptions)` → `EquipAsync`/`UnequipAsync` move items in/out of `InventoryServer` and apply the item's `StatModifier`s (from `EquipmentOptions.ItemStats`) to the wearer's `StatSet` (source = `equip:<slot>`, removed on unequip); `client.UseEquipment()` → equip/unequip/query + `Changed`. Pluggable `IEquipmentStore`. `EquipmentRuntime.Enable()`.

- **AI building blocks (pure libraries, deps `SetNet` only, no wire)**: **`SetNet.StateMachine`** — generic `StateMachine<TContext>.Build().State(...).Transition(from,to,guard).Create()`, `Start`/`Update(ctx,dt)`/`GoTo`, any-state transitions. **`SetNet.BehaviorTree`** — `BehaviorTree<TContext>.Build()` fluent tree: `Sequence`/`Selector`/`Parallel` composites, `Inverter`/`Succeeder`/`Repeat`/`Cooldown` decorators, `Do`/`Condition`/`Wait` leaves, `End()`/`Create()`; `Tick(ctx,dt)` → `BtStatus`. Wrap either in an `IMobBrain` for mob AI, or use anywhere.

- **Spawning (optional, separate package `SetNet.Spawning`; depends on `SetNet.Mobs` + `SetNet.GeoData`; server-side, no wire)**: zone-based mob population over `SetNet.Mobs`. `SpawnZone.Circle(id, center, radius)`/`Box(id, min, max)` `.Add(mobType, count, respawnMs, health?, faction?)`; `server.UseSpawning(mobs, SpawnOptions).AddZone(zone)`. Each tick (internal timer or headless `Update(dtMs)`) it keeps each population at its target, detects deaths (a tracked id no longer `IsAlive`), and respawns after the delay; spawns with the mob's own `RespawnMs=0` so this owns timing, random points snapped to walkable via `SpawnOptions.GeoData`. `SpawningRuntime.Enable()` (no-op).

- **Account / character / game-data / login (MMO entry stack; server-side unless noted)**:
  - **`SetNet.Accounts`** (deps `SetNet` + `SetNet.Persistence`; no wire): `AccountServer<TAccount>` over any `IDocumentStore` (accounts keyed by id + a case-insensitive username→id index). `RegisterAsync`/`AuthenticateAsync` (→ `AccountAuthStatus` Ok/UnknownUser/WrongPassword/Banned)/`Ban`/`Unban`/`SetPassword`. Pluggable `IPasswordHasher` (default `Pbkdf2PasswordHasher`, SHA-256 100k, constant-time verify). Subclass `AccountBase` (or use its `Extra` bag) for custom fields — stored whole, no schema change.
  - **`SetNet.CharacterStore`** (deps `SetNet` + `SetNet.Persistence`; no wire): `CharacterServer<TChar>` over any `IDocumentStore`. `CreateAsync` (assigns Id/AccountId/CreatedUnix, enforces `MaxPerAccount` slots + optional global unique name)/`ListAsync(accountId, includeDeleted?)`/`Get`/`Save`/`Rename`/`SoftDeleteAsync`+`RestoreAsync` (restore window)/`PurgeAsync`. Subclass `CharacterBase` for custom fields (e.g. `VipUntil`). **Scale note:** list/uniqueness scan `AllAsync` — swap for indexed DB queries at scale.
  - **`SetNet.GameData`** (deps `System.Text.Json` only; no wire, client+server): `GameDataRegistry` builds id-keyed `DataTable<TId,TRow>` (`Get`/`TryGet`/`All`/`Ids`/`Count`) from **any source** — `LoadJson`/`LoadFile` (files), `Load(name, IEnumerable, key)` (one-shot, e.g. a DB query / `IDocumentStore.AllAsync()`), or `LoadFrom(name, Func<IEnumerable>, key)` (reloadable source — `Reload()` re-runs it, so it re-queries Postgres/Dapper/Mongo). `Reloaded` event. Rows are your POCOs (any custom columns). No DB dependency — the caller supplies the query.
  - **`SetNet.LoginServer`** (deps `SetNet`; wire `Channels.Login` = 32): L2-style login coordinator. `server.UseLoginServer(LoginOptions{ Authenticate delegate (wire to Accounts), Servers func (feed from LoadBalancer), Tokens=ILoginTokenStore, TokenTtlSeconds })` + `client.UseLogin()` → `LoginAsync`(→`LoginStatus`)/`ServerListAsync`/`SelectServerAsync`(→ one-time token + host:port). The game server consumes the token via the **shared** `ILoginTokenStore.ConsumeAsync` (default in-process `MemoryLoginTokenStore`; use Redis/DB across nodes). Hand-framed codec (serializer-agnostic). Per-peer login state cleared on `PeerDisconnected`. `LoginRuntime.Enable()`.

- **BoardGame — turn-based board/card framework (optional, separate package `SetNet.BoardGame`; deps `SetNet`; engine has no wire yet)**: card primitives (`Card`/`Suit`/`Rank`, `Decks.Standard36/52`, deterministic `Shuffle`) + the server-authoritative contract **`ITurnGame<TState,TMove,TView>`** (`Seats`/`Start(players,seed)`/`CurrentSeat`/`LegalMoves`/`Apply`(validates, throws `GameException`)/`View`(per-player redacted — hidden hands)/`Outcome`→`GameOutcome{Winners,Losers}`) + headless `TurnGameHost<…>` (drives a match; used by bots/tests/a future wire hub). Ships a complete deterministic **Durak** engine: `DurakGame(seats 2..6)` + `DurakState`/`DurakMove`(Attack/Defend/Take/Done)/`DurakView`(own hand full, others by count). 36-card deck, trump from bottom card, deal 6, attack/defend/throw-in/take/Бито, refill to 6, last holder = durak. Simplifications: attacker-only throw-ins, sequential bout, no perevodnoy (documented). `Channels.BoardGame=33` reserved for the future networked hub (tables + per-player view push). Example `examples/Durak` (full 2-bot game, each bot sees only its `View`).

- **Ticks — central update loop (optional, separate package `SetNet.Ticks`; ZERO dependencies — a foundation the game-loop modules depend on, *not* core; server-side, no wire)**: one place to drive every periodic update, so nothing needs its own timer or a hand-written `Tick()` call. Deliberately kept OUT of core (core stays a pure transport/data layer): the whole tick foundation — the unifying contract **`ITickable { void Tick(in TickInfo) }`** (+ async `IAsyncTickable`), `TickInfo{DeltaMs, DeltaSeconds, Frame}`, the `ITickHost` registration seam + ambient `TickHost.Current`, and the `TickScheduler` — all live in the **`SetNet.Ticks`** package (namespace `SetNet.Ticks`, no `SetNet` core reference). The game-loop modules (`SetNet.Mobs`/`.Locomotion`/`.Spawning`) and AI libs (`.BehaviorTree`/`.StateMachine`) reference `SetNet.Ticks` to implement the contract. `ticks.Channel(name, hz, priority)` → a **channel** with its own rate + priority; `channel.Add(ITickable | IAsyncTickable | Action<TickInfo> | Func<TickInfo,Task>)` (returns an `IDisposable` to unregister). Each channel advances on a **fixed timestep** (`DeltaMs = 1000/Hz`) via an accumulator, in priority order (high first), capped by `MaxSubstepsPerPump` (anti-spiral). Drive it with `Start(baseHz)` (internal timer) **or** `Pump(realDtMs)` from your own loop (Unity `Update`/`FixedUpdate`). **Automatic subscription (the default path):** the `ITickHost` seam + ambient `TickHost.Current` (in `SetNet.Ticks`) — call `new TickScheduler().MakeCurrent()` **before** your `UseXxx(...)` and the game-loop systems subscribe *themselves* on construction, each into its own channel, **instead of** running their own timer: `LocomotionSystem` → `"locomotion"`@`Hz`/prio 100, `MobServer` → `"mobs"`@`TickRateHz`/prio 50, `SpawningServer` → `"spawning"`@`1000/TickIntervalMs`/prio 10 (all overridable via `xxxOptions.TickChannel`/`TickPriority`; opt out with `AutoTick=false`, which falls back to the internal timer). So you never register each mob/mover/system — every mob rides `MobServer`, every mover rides `LocomotionSystem`. The precedence in each ctor: `AutoTick && TickHost.Current != null` → register with host (no timer); else `UseInternalTimer` → own timer; else manual. Implementation: `LocomotionSystem`+`SpawningServer` are `ITickable`, `MobServer` is `IAsyncTickable` (self-guards overruns); each stores the registration `IDisposable` and unsubscribes on `Dispose`. **Behaviour trees / state machines** (which need a context, so can't auto-subscribe blind) plug in via `tree.Bind(ctx)` / `fsm.Bind(ctx)` → `ITickable` — but trees inside mob brains already ride `MobServer`, so `Bind` is only for standalone ones. Manual path still works (skip `MakeCurrent()`, set `AutoTick=false`+`UseInternalTimer=false`, `channel.Add(loco/mobs)`). Housekeeping timers in other modules (Auth sweep, Auction settle, Matchmaking, StatusEffects) stay independent — route through a `slow` channel only if you want one clock. No `Runtime.Enable()` needed. (Note: `TickHost.Current` is process-global, so the unit-test assembly disables xUnit parallelization — `tests/SetNet.UnitTests/TestParallelization.cs`.)

- **Notifications (optional, separate package `SetNet.Notifications`; depends only on `SetNet`; `Channels.Notifications`)**: server→client toasts. `server.UseNotifications(INotificationStore?)` → `NotifyAsync(playerKey, Notification)` (pushed if online, else queued) / `BroadcastAsync(...)`; offline queue flushed on reconnect (`PeerConnected`). `client.UseNotifications()` → `Received` event. Push-only (no server-side channel service needed). `NotificationRuntime.Enable()` (no-op).

- **Dialogue (optional, separate package `SetNet.Dialogue`; depends only on `SetNet`; `Channels.Dialogue`)**: server-authoritative branching dialogue. `DialogueTree.Create().Node(id, text, DialogueChoice(text, next, condition?, onChosen?)…).Build()`; `server.UseDialogue().Define(id, tree)`; `client.UseDialogue().StartAsync(id)` / `ChooseAsync(index)` → `DialogueNodeView` (visible choices filtered by guards; `IsEnd` when a choice leads nowhere). Per-player conversation state, cleared on disconnect. Pairs with `SetNet.NPC` capability hand-off `"dialogue:<id>"`. `DialogueRuntime.Enable()`.

- **Persistence (optional, separate package `SetNet.Persistence`; depends on `SetNet` + `System.Text.Json`; `LangVersion=9.0` for unconstrained `T?`; no wire)**: a durable-state seam. `IDocumentStore<T>` (`Get`/`Set`/`Remove`/`Exists`/`Keys`/`All`) with `MemoryDocumentStore<T>` + `JsonFileDocumentStore<T>` (single JSON key→value file, atomic-ish replace); `ISnapshotStore` (opaque byte blobs) with memory + file impls. Back inventories/wallets/progression/save-games/world snapshots with something durable, then swap for Redis/DB behind the same interface. **DB adapter packages** (all `net8.0`, implement the same seam, store the whole POCO so custom fields need no schema change): `SetNet.Persistence.Dapper` (any ADO.NET DB, JSON in a key/value table, portable delete+insert upsert), `SetNet.Persistence.Postgres` (Npgsql, **JSONB** column → custom fields stay SQL-queryable/indexable, `ON CONFLICT` upsert), `SetNet.Persistence.EfCore` (provider-agnostic via `DbContextOptions` + `SetNetPersistenceContext`, `SetNetDocuments`/`SetNetSnapshots` tables), `SetNet.Persistence.MongoDB` (MongoDB.Driver, native BSON, `ReplaceOne` upsert). Dapper+EfCore covered by real in-process SQLite tests incl. custom fields.

- **Docs (optional, separate package `SetNet.Docs`; depends only on `SetNet`; tooling, no wire)**: `ProtocolDocs.Generate(assemblies?)` reflects `[MessageHandler]` (type id/direction/payload), `[ProtocolChannel]` + `[Op]`/`[Event]` (channel id + ids, channel names from `SetNet.Protocol.Channels`), and `[RpcMethod]` (matched by attribute name → no `SetNet.Rpc` dep) into a `ProtocolReport.ToMarkdown()`. All per-type reflection is exception-guarded (skips unloadable test-host types). `GenerateMarkdown()` / `WriteMarkdown(path)`.

- **Voice relay (optional, separate package `SetNet.Voice`; depends only on `SetNet`)**: codec-agnostic voice hub. `server.UseVoice()` fans each `VoiceUp` (65504) frame out to the sender's channel members as `VoiceDown` (65503) tagged with a stable per-peer speaker id; `VoiceControl` (65505) is join/leave. `var voice = client.UseVoice()` → `JoinChannel`/`LeaveChannel`/`SendFrame(ch, byte[])` + `FrameReceived(speakerId, ch, audio)`. Opaque audio bytes (Opus/PCM), **unreliable** delivery, no server-side mixing. Co-located clients share the static dispatch (one-client-per-process typical). `VoiceRuntime.Enable()`.

- **Relay (optional, separate package `SetNet.Relay`; depends only on `SetNet`)**: TURN-style opaque relay hub (reserved types 65498/65499/65500). `server.UseRelay()` + `var relay = client.UseRelay()` → `AllocateAsync(maxPeers)`/`JoinAsync(code)`/`LeaveAsync`/`SendAsync(bytes)`/`SendToAsync(peerId, bytes)` + `Received(fromPeerId, bytes)`/`PeerJoined`/`PeerLeft`/`Closed`. Hand-framed byte[] protocol with correlated Command/Reply + push Event, mirroring Rooms (server sends Reply/Event via `SendAsync` — serializer-wrapped — to match the `IClientMessageHandler<byte[]>` invoker, **not** `SendRawAsync`). Node-local sessions; per-sender peer ids; opaque forwarding for NAT fallback/tunnelling. `RelayRuntime.Enable()`.

- **Redis backplane (optional, separate package `SetNet.Redis`; depends on `SetNet.Auth` + `SetNet.Rooms` + `SetNet.BanList` + `StackExchange.Redis`)**: Redis-backed store implementations so state is shared across nodes / survives restarts. `RedisSessionStore : ISessionStore` (→ `AuthOptions.SessionStore`; TTL via key expiry, token rotation preserved, `SweepAsync` no-op; `Session.LivePeer` not persisted → cross-node `KickExisting` needs a side channel), `RedisBanStore : IBanStore` (sync StackExchange.Redis API; timed bans use key TTL), `RedisRoomStore : IRoomStore` (atomic code reservation via `StringSet When.NotExists`; shares codes/metadata only — members stay node-local). Share one `IConnectionMultiplexer`; `RedisStores.Connect(cfg)` convenience. No wire types.

- **NAT punch-through (optional, separate package `SetNet.NatPunch`; depends only on `SetNet`)**: UDP hole-punching by **composition** (reserved types 65495/65496/65497). `server.UseNatPunch()` runs a coordinator: the host `RegisterAsync(udpPort)` → code; the guest `PunchAsync(code, udpPort)`; the coordinator pushes both sides the counterpart's candidates (public = server-observed IP + reported UDP port, empty when the transport exposes no remote endpoint; private = NIC enumeration) back-to-back so they punch simultaneously. `NatPuncher.TryPunchAsync(localPort, target)` fires magic-tagged probe/ack datagrams at all candidates and returns the opened endpoint (or null → fall back to `SetNet.Relay`; symmetric NATs won't punch). Co-located clients filter events by session code **and role** (a host consumes guest candidates and vice versa). `NatPunchRuntime.Enable()`.

- **UPnP port mapping (optional, separate package `SetNet.Upnp`; depends only on `SetNet`)**: no wire types, no server piece — talks SSDP (M-SEARCH for IGD v1/v2) + SOAP (WANIPConnection:1/:2, WANPPPConnection:1) to the LAN router. `UpnpPortMapper.DiscoverAsync()` → `UpnpDevice` (`GetExternalIpAsync`/`AddPortMappingAsync`/`DeletePortMappingAsync`), plus `config.MapServerPortsAsync()` mapping whatever the `Configuration` listens on (TCP port, UDP port per `TransportType`). Parsing lives in internal `UpnpXml` (unit-tested via `InternalsVisibleTo`); note the Unix gotcha handled there — a relative `controlURL` like `/ctl` parses as an absolute `file://` URI, so absolute control URLs are scheme-checked.

- **Multiplex (optional, separate package `SetNet.Multiplex`; depends only on `SetNet`)**: logical channels over one connection (reserved type 65494; envelope `[1 channel][2 origType LE][payload]`). `SendMuxAsync(channel, type, msg)` on client/peer; the receiving side demuxes into **per-channel FIFO lanes** (one drain task per lane, Interlocked hand-off) and re-injects via `InjectFrame`, so the original typed handlers fire unchanged — ordering preserved within a channel, independent across channels (dispatch head-of-line-blocking fix; complements `UdpReliableChannels`, which separates *retransmission* streams). Client receive side opts in with `client.UseMultiplex()`; the server side demuxes per peer automatically. `MultiplexRuntime.Enable()`.

- **Streams (optional, separate package `SetNet.Streams`; depends only on `SetNet`)**: large-payload transfer both directions (reserved types 65492 data / 65493 control). Offer(name, length) → receiver accepts (event `OfferReceived` with `AcceptAsync(IStreamSink)`/`RejectAsync`, or auto-accept ≤ `MaxAutoAcceptBytes` into a `MemoryStreamSink`; `FileStreamSink` for disk) → sequential contiguity-validated chunks (requires a reliable+ordered path) → Complete handshake. Sender: `client.UseStreams().SendAsync(name, stream, IProgress<double>?, streamId?)` / `server.UseStreams().SendAsync(peer, ...)`; **resume**: the receiver parks interrupted partials for `PartialTtlSeconds`, the Accept reply carries the resume offset, and re-sending with the same `streamId` (from `StreamsException.StreamId`) re-sends only the tail. Sender-side correlation is a global `PendingSends` map keyed by GUID stream id. `StreamsRuntime.Enable()`.

- **Sharding (optional, separate package `SetNet.Sharding`; depends only on `SetNet`)**: consistent-hash key→node routing (reserved types 65490/65491). `ShardRing` (immutable; FNV-1a-64 over NodeId/key, `VirtualNodes` per node, `GetNode`/`GetNodes(key,n)` for replicas) + `server.UseSharding(ShardingOptions{Nodes, SelfNodeId})` → a directory any node answers plus in-process `Locate`/`IsLocal`; `client.UseSharding().LocateAsync(key)` → connect to the owning node (a directory, not a proxy — for transparent forwarding put `SetNet.Gateway` in front). Membership changes via `UpdateNodes` (atomic ring swap, ~1/N keys remap) — broadcasting them (e.g. over `SetNet.Cluster`) is the app's job. `ShardingRuntime.Enable()`.

- **Economy + progression suite (optional, separate packages; identity/online lookup reuse `InventoryServer.KeyOf`/`PeerFor` or `WalletServer.KeyOf` where a dep exists, so no duplicate resolvers)**:
  - **`SetNet.Wallet`** (deps `SetNet`; types 65475/65476/65477): server-authoritative currencies mirroring Inventory. `server.UseWallet()` → `DepositAsync`/`TryWithdrawAsync`/`TryTransferAsync`/`GetAsync` by player key (atomic anti-overdraft; transfers serialize on one gate); `client.UseWallet()` → read + `Changed` push. Pluggable `IWalletStore`. `WalletRuntime.Enable()`.
  - **`SetNet.Vendor`** (deps `SetNet`+`SetNet.Inventory`+`SetNet.Wallet`; types 65473/65474): NPC shops. `server.UseVendor(inventory, wallet).Define(vendorId, entries)`; `client.UseVendor()` → `ListAsync`/`BuyAsync`/`SellAsync`. Buy reserves stock → `Wallet.TryWithdraw` → `Inventory.Grant` (stock refunded on charge failure); sell `Inventory.TryRevoke` → `Wallet.Deposit`. `VendorRuntime.Enable()`.
  - **`SetNet.Auction`** (deps `SetNet`+`SetNet.Inventory`+`SetNet.Wallet`; types 65470/65471/65472): auction house. Item escrowed on list, currency escrowed on bid (prior bidder refunded via `Outbid`), a `Timer` (1 s) settles expired listings — winner gets item, seller gets bid, else item returned. `AuctionServer : IDisposable` (Dispose stops the timer). `Won`/`Sold`/`Returned`/`Outbid` events pushed via `Inventory.PeerFor`. Per-listing `Gate` + `Settled` claim make settlement/cancel exactly-once. `AuctionRuntime.Enable()`.
  - **`SetNet.Crafting`** (deps `SetNet`+`SetNet.Inventory`; types 65467/65468): `server.UseCrafting(inventory).Define(Recipe{inputs,outputs})`; `client.UseCrafting()` → `CraftAsync(recipeId, times)`/`ListAsync`. Revokes all inputs (compensating re-grant on shortfall) then grants outputs. `CraftingRuntime.Enable()`.
  - **`SetNet.Loot`** (deps `SetNet`+`SetNet.Inventory`; types 65465/65466): weighted drop tables (guaranteed entries + `Rolls` weighted draws, RNG server-side, optional `Seed`). `server.UseLoot(inventory).Define(LootTable).RollAndGrantAsync(playerKey, tableId)`; client `OpenAsync` gated by `LootOptions.CanOpen` (default deny). `LootRuntime.Enable()`.
  - **`SetNet.Quests`** (deps `SetNet`+`SetNet.Inventory`; types 65462/65463/65464): `server.UseQuests(inventory).Define(QuestDefinition{objectives,rewards})`; game logic `ProgressAsync(playerKey, objectiveKey, amount)` fans out to every accepted quest with that key; `QuestCompleted` event on first completion; `ClaimAsync` grants item rewards. Client `AcceptAsync`/`AbandonAsync`/`ClaimAsync`/`ListAsync` + `Updated` push. Pluggable `IQuestStore`. `QuestRuntime.Enable()`.
  - **`SetNet.Progression`** (deps `SetNet`; types 65459/65460/65461): levels/XP. `server.UseProgression(options{XpForLevel, MaxLevel})` → `GrantXpAsync` (multi-level rollover, clamps at cap), `LeveledUp` event per level (app grants rewards there); `client.UseProgression()` → read + `Changed` push. Pluggable `IProgressionStore`. `ProgressionRuntime.Enable()`.
  - **`SetNet.Guilds`** (deps `SetNet`+`SetNet.Inventory`; types 65456/65457/65458): roles `Member/Officer/Leader`. `server.UseGuilds(inventory)`; `client.UseGuilds()` → create/join/leave/promote/kick/listMembers + bank deposit/withdraw/list + `MemberJoined`/`MemberLeft`/`Disbanded`. **Bank is a guild-keyed inventory** (`guild:<id>` in the same `InventoryServer`) so deposits/withdrawals are atomic; deposit any member, withdraw/kick officer+, promote/disband leader; leader leaving auto-promotes the highest remaining, last member disbands and gets the bank back. Pluggable `IGuildStore`. `GuildRuntime.Enable()`.
  - **`SetNet.Marketplace`** (deps `SetNet`+`SetNet.Inventory`+`SetNet.Wallet`; types 65451/65452/65453): continuous double-sided order book per (item, currency). `client.UseMarketplace()` → `PostBuyAsync`/`PostSellAsync`/`CancelAsync`/`GetBookAsync`/`MyOrdersAsync` + `Filled` push. Resource escrowed on post (buy → `Wallet.TryWithdraw` price×qty; sell → `Inventory.TryRevoke` qty). Matching under a per-book lock (price-time priority; best price, ties by earliest `Seq`); **trade executes at the resting order's price**, so an incoming order never does worse than its limit and the buyer is refunded `(limit − tradePrice)×qty`. Item/currency moves + fill notifications (via `Inventory.PeerFor`) run after the lock. Cancel returns remaining escrow. Node-local books. `MarketplaceRuntime.Enable()`.
  - **`SetNet.StatusEffects`** (deps `SetNet`; types 65448/65449/65450): server-authoritative buffs/debuffs on any **target key** (player key or entity id like `mob:dragon#7`). `server.UseStatusEffects(options{TargetKey, TickIntervalMs}).Define(StatusEffectDefinition{MaxStacks, DefaultDurationMs, Stacking, IsDebuff})` → `ApplyAsync`/`RemoveAsync`/`ClearAsync`/`GetAsync`. Stacking policy `Refresh`/`Stack`/`Ignore`. A `Timer` (default 250 ms) expires effects and pushes. Pushes go to the affected player if online (`_online` from `TargetKey`) **and** to explicit watchers — `client.UseStatusEffects()` → `GetAsync`/`WatchAsync`/`UnwatchAsync(targetKey)` + `Changed(targetKey, effects)` (so players fighting a boss see its debuffs). Watchers cleared on disconnect. `StatusEffectServer : IDisposable` (Dispose stops timer). Effects are data; magnitude is game-interpreted. Not persisted. `StatusEffectRuntime.Enable()`.
  - **`SetNet.LoadBalancer`** (deps `SetNet`; types 65454/65455): `server.UseLoadBalancer()` keeps a node registry (`UpdateNode`/`ReportLoad`/`RemoveNode`, fed by the app from `SetNet.Cluster`/orchestrator — no built-in gossip); `client.UseLoadBalancer().PickAsync()` returns the lowest load-ratio node with capacity (throws when all full). Capacity-driven counterpart to Sharding's key-driven routing. `LoadBalancerRuntime.Enable()`.

- **Cluster bus (optional, separate package `SetNet.Cluster`; depends only on `SetNet`)**: server-to-server broadcast mesh, isolated on its own listener/port (reserved type 65501). `new ClusterNode(ClusterNodeOptions{NodeId, ListenPort, Seeds})` runs a dedicated `BaseServer` (do **not** await its `StartAsync` — it's the accept loop) and dials each seed with a self-managed `ClusterLink : BaseClient` that retries the *initial* connect and re-dials on drop (`AutoReconnect=false`, own maintain loop via `OnDisconnected`). `Publish`/`Publish<T>`/`On<T>`/`Received`/`Stop`. Uses `OnRawFrame`/`SendRawAsync` (no handler discovery); best-effort broadcast, no consensus. `ClusterRuntime.Enable()` is a no-op.

- **Custom transport registration (core hook, general-purpose)**: `TransportType.Custom` + `Configuration.CustomTransport : ITransportProvider` (`CreateConnector`/`CreateListener`), resolved in `TransportFactory`. Lets an external package supply the transport without editing core — used by `SetNet.WebSockets` and `SetNet.InMemory`; also the path for future WebRTC/QUIC/Steam transports.

- **State replication (optional, separate package `SetNet.StateSync`; depends only on `SetNet`; Unity binding `SetNet.StateSync.Unity` is a UPM source package)**: server-authoritative entity replication by **composition**. Engine-agnostic core (headless server + any .NET client). `server.UseStateSync(StateSyncOptions)` → `ServerReplication` (`Spawn`/`Despawn`/`AddObserver`/`InputReceived`); `client.UseStateSync()` → `ClientReplication` (`EntitySpawned`/`EntityDespawned` events, `Entities`, `OwnedEntity`, `SendInput`, `Update()` for interpolation). Archetypes are field schemas registered identically on both ends via `ReplicaRegistry` (`ReplicaSchema.Create(id).Field(FieldType,...)`); the framework interpolates float/vector/quaternion fields and snaps the rest, with optional per-field float quantization. A fixed-rate `Timer` tick sends **delta-compressed** snapshots (Quake-3-style tick/baseline history, per-observer ack) over `DeliveryMethod.Unreliable`; **Spawn/Despawn go `Reliable`**. Interest management is pluggable (`IInterestManager`: `AllInterest` default, `DistanceInterest`). An input channel (`SendInput`→`InputReceived`, `LastProcessedInput` echo) provides prediction plumbing (rewind/replay is game-specific). Reserved wire types 65518–65522. Enabled by the general-purpose core hook `BaseServer.PeerConnected` (symmetric with `PeerDisconnected`, fires once per peer after StartReceive) for auto-observe. Call `StateSyncRuntime.Enable()` at startup. **Unity layer** (`SetNet.StateSync.Unity`, UPM/UnityEngine, not NuGet): `NetworkObject` (archetype schema = ordered concat of its `INetworkComponent`s' fields) + `NetworkTransform` (pos/rot/scale, quantize, owner-authoritative) + `NetworkAnimator` (all controller params + triggers-as-pulse-counter + optional layer state) + `NetworkRigidbody` (velocities, kinematic on non-owners) + `NetworkBehaviour` (custom fields, AOT-safe) + `NetworkManager` (prefab registry, spawn/despawn, drives tick/interp, marshals via `SetNet.Unity.MainThreadDispatcher`).

- **Additional companion packages (see [docs/MODULES.md](docs/MODULES.md) for the full catalog)** — all composition, same patterns. Serializers via `ISerializer`: `SetNet.Json`, `SetNet.MemoryPack`, `SetNet.Protobuf`, `SetNet.Compression` (Brotli decorator). Security: `SetNet.Auth.Jwt`/`SetNet.Auth.OAuth` (`IAuthenticator`s), `SetNet.BanList` (ban gate via `InboundAuthorizer` + kick, `IBanStore`), `SetNet.DdosGuard` (per-IP flood auto-ban on `SetNet.BanList`). Net/QoS: `SetNet.Fragmentation` (oversize-UDP split/reassemble; reserved 65517; uses `InjectFrame`), `SetNet.Priority` (`PrioritySender`), `SetNet.Congestion` (AIMD `CongestionController`). Realtime: `SetNet.Rooms.HostMigration` (uses `RoomServer.RoomHooks()`), `SetNet.Party`, `SetNet.Chat`, `SetNet.Lockstep` (all four now ride the unified `SetNet.Protocol` on their `Channels` id — see the Unified messaging protocol bullet), and StateSync sub-modules `SetNet.StateSync.{SpatialGrid,LagCompensation,Prediction,NetworkVariable,Rpc}` (Rpc reserved 65516). Infra: `SetNet.DependencyInjection` (`provider.UseSetNet()` routes construction of **all** reflection-discovered components — `[MessageHandler]`, `[ProtocolChannel]` channel services/`[Op]` classes, client `[Event]` handlers, `[RpcMethod]` RPC handlers — through the one core `HandlerActivator.Factory` seam, with constructor injection via `ActivatorUtilities`; `UseSetNetHandlers()` kept as alias), `SetNet.Services` (tiny service **locator** — `ServiceHub`: `hub.Add(server.UseXxx())` once, resolve anywhere by type via ambient `Service.Get<T>()` (`MakeCurrent`) or per-owner `server.Services()`/`client.Services()` (`ConditionalWeakTable`); locates the module singletons, constructs nothing; deps core only), `SetNet.Hosting` (`IHostedService`), `SetNet.HealthChecks` (`IHealthCheck`), `SetNet.Inspector` (HttpListener `/metrics`+HTML dashboard), `SetNet.Gateway` (raw-relay reverse proxy via `OnRawFrame`/`SendRawAsync`). Engine: `SetNet.Godot` (Godot 4 dispatcher + math, deps `GodotSharp`).

- **Core hooks added for these (all general-purpose, additive)**: `PeerInfo.RemoteEndPoint`+`BasePeer.RemoteEndPoint` (ban/DDoS/inspector/gateway), `BaseSocket.InjectFrame(type,data)` (fragmentation reassembly / relays), `HandlerActivator.Factory` (the one DI construction seam for ALL discovered components — handlers, `[ProtocolChannel]`/`[Op]`, client `[Event]`, `[RpcMethod]` — used by `SetNet.DependencyInjection`), `RoomServer.RoomHooks()` (Rooms server-side join/left events for host migration). **Reserved wire-type map:** the 24 command/reply/event companion modules were consolidated onto the single `SetNet.Protocol` envelope id **65447** (demultiplexed by per-module `Channels` ids — see the Unified messaging protocol bullet). The remaining individually-reserved ids belong to the shape-different modules that stayed off the unified protocol (`SetNet.StateSync`(+`.Rpc`), `SetNet.Voice`, `SetNet.Fragmentation`, `SetNet.Multiplex`, `SetNet.Streams`, `SetNet.Cluster`, `SetNet.Auth`, `SetNet.ProofOfWork`) plus the core system types (65533–65535). (`SetNet.Rpc` now rides the 65447 envelope on `Channels.Rpc`.) (see MODULES.md).

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
