# AGENTS.md

This file provides guidance to coding agents (Codex, Claude Code, and similar) when working with code in this repository. It is kept in sync with `CLAUDE.md`.

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
dotnet test SetNet.UnitTests/SetNet.UnitTests.csproj
```

**Run the in-process transport scenarios (manual harness):**
```bash
dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
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

- **BaseSocket**: Foundation class providing message-processor integration. Holds an `ITransportConnection` (the transport-agnostic channel) and routes received frames to handlers via `HandleMessage`. Owns the dispatch gate (`MaxInFlightMessages` back-pressure, re-armed per connection generation) and the optional sequential-dispatch mode.

- **Transport abstraction** (`SetNet/Core/Transport/`): `ITransportConnection`/`ITransportConnector`/`ITransportListener` plus implementations in `Tcp/`, `Udp/`, and `Both/`. TCP wraps `NetworkStream`/`SslStream` + length-prefix reassembly (reuses `PacketBuilder`), with optional send batching and a `SendTimeoutMs` per-write deadline. UDP wraps a datagram socket with an emulated-connection handshake, heartbeat, peer demux by endpoint, and an optional `ReliabilityChannelSet` (one or more `ReliabilityChannel`s: sequence/ACK/retransmit/ordered). `Both` is a composite that runs TCP and UDP together. `TransportFactory` builds the right pieces from `Configuration`.

- **BaseClient**: Abstract client implementation that connects to a server, handles incoming messages, and manages the connection lifecycle (connect, heartbeat, unexpected-loss detection, optional auto-reconnect, disposal). Subclasses implement `OnConnected()`, `OnDisconnected()`, `OnError()` and the reconnect hooks.

- **BaseServer**: Abstract server implementation that listens for incoming connections and delegates client handling to abstract peer objects. Manages a pool of connected clients; calls the idempotent `peer.StartReceive()` itself after `OnNewClient`.

- **BasePeer**: Abstract server-side peer representing a connected client. Handles incoming data from that specific client and sends responses back. `Close()` fires `OnDisconnected` exactly once.

The flow: Server accepts connection → creates a BasePeer → peer receives messages → messages routed to handlers → handlers process and respond.

### 2. **Message Handling Framework** (`SetNet/Core/Commands/` + `SetNet/Data/`)

- **ServerCommandExecutor / ClientCommandExecutor**: Use reflection to auto-discover and register message handlers at startup. Look for classes implementing `IServerMessageHandler<T>` or `IClientMessageHandler<T>` decorated with `MessageHandlerAttribute`, instantiate each, and wrap it in an internal invoker (`ServerHandlerInvoker<T>`/`ClientHandlerInvoker<T>`, in `HandlerInvoker.cs`) that deserializes the payload via `SetNetSerializer` and calls the typed handler. Discovery is cached per handler interface in `HandlerDiscovery`.

- **MessageHandlerAttribute**: Marks a handler class and specifies its message type (ushort). Used by the executors for reflection-based registration.

- **IServerMessageHandler\<TMessage\>**: Interface for handlers that process messages on the server side. Signature: `Task HandleAsync(BasePeer peer, TMessage message)` — the library deserializes the payload into `TMessage` before calling.

- **IClientMessageHandler\<TMessage\>**: Interface for handlers that process messages on the client side. Signature: `Task HandleAsync(TMessage message)`.

Message handlers are **strongly typed** (no manual deserialization) and are discovered and instantiated automatically via reflection when the executor is constructed.

- **Raw frame escape hatch**: `BaseSocket.OnRawFrame(ushort type, byte[] data)` (virtual, default no-op) is called for every application frame (system types excluded via `SystemMessageTypes.IsSystem`) before typed dispatch; returning `true` consumes the frame and skips typed handling. Paired with `BaseClient`/`BasePeer.SendRawAsync(type, payload, delivery?)` which sends already-serialized bytes without re-serializing. Together they enable relay/proxy peers that forward traffic with zero (de)serialization while normal handlers stay typed. `SendAsync`/`SendRawAsync` are **public** on `BaseClient`/`BasePeer` (not just protected) so companion packages/plugins can send without subclassing. **Dispatch ordering is not guaranteed by default** (handlers run fire-and-forget); set `Configuration.SequentialDispatch = true` for strict in-order, non-overlapping handler execution on a single connection.

- **RPC (optional, separate package `SetNet.Rpc`; depends only on `SetNet`, serializer-agnostic)**: request/response by **composition**, no base class. `await client.CallAsync<TReq,TResp>(methodId, req)` (extension) + `[RpcMethod]` `IRpcHandler<TReq,TResp>` server handlers. The envelope is hand-framed as a `byte[]` (rides over any serializer, no MessagePack dependency); bodies go through `SetNetSerializer`. It ships auto-discovered `[MessageHandler]`s (`IServerMessageHandler<byte[]>`/`IClientMessageHandler<byte[]>`) for reserved envelope type ids (65531/65532), completes calls via a static correlation-id registry, relays server exceptions as `RpcException`, and enforces per-call timeouts. Call `RpcRuntime.Enable()` once at startup so the package is loaded before handler discovery.

- **Auth + sessions (optional, separate package `SetNet.Auth`; depends only on `SetNet`, serializer-agnostic)**: authentication by **composition**. `server.UseAuth(IAuthenticator, AuthOptions)` installs an **enforced inbound gate** — until a peer authenticates, its application frames (regular + RPC) are dropped; only the auth handshake (reserved types 65529/65530) passes. `client.UseAuth(tokenProvider)` auto-authenticates on connect and every reconnect (via the core `BaseClient.Connected` event), with reconnect-token session resume (rotating single-use tokens, TTL) and multi-session policy (AllowMultiple/KickExisting/RejectNew). Sessions live in a pluggable `ISessionStore` (default in-process `MemorySessionStore`; supply a Redis/DB store for cross-restart/cluster). Enabled by three small public core hooks added for it (general-purpose): `BaseClient.Connected` event, `BaseServer.InboundAuthorizer` (consulted per app frame via `BaseSocket.AllowInbound`/`BasePeer` override), and public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`. Call `AuthRuntime.Enable()` once at startup. Use over TLS.

### 3. **Serialization** (`SetNet/Messaging/`)

- **ISerializer / SetNetSerializer**: Pluggable serialization seam. The core bundles **no** serializer; one is registered once at startup via `SetNetSerializer.Use(ISerializer)` (the backing instance is not exposed publicly). The library uses it everywhere through the `SetNetSerializer.Serialize/Deserialize` façade — both the send path and the typed-handler receive path. The MessagePack adapter (`MessagePackNetSerializer`, `UntrustedData`-hardened) lives in the separate **SetNet.MessagePack** project/package. Until a serializer is registered, the façade throws a clear error.

- **PacketBuilder**: Encodes messages into the wire protocol (4-byte length prefix + 2-byte type) and reassembles incoming data into complete packets. Rejects negative/0/1 declared lengths and frames over `MaxMessageSize`.

- **MessageProcessor**: Routes incoming byte[] messages to registered async/sync handlers by type identifier. Handler faults are reported via `OnHandlerError` through a crash-safe path (a throwing logger cannot escape the async-void observer).

### 4. **Configuration** (`SetNet/Config/`)

- **Configuration**: Holds connection settings (Host, Port, BufferSize, MaxConnections), reconnection options (AutoReconnect, MaxReconnectAttempts, ReconnectDelayMs), heartbeat options, **transport options** (`TransportType` Tcp/Udp/Both, `DefaultDelivery`, `UdpPort`, UDP handshake/expiry timeouts, the UDP reliability layer settings incl. `UdpReliableChannels`), **TLS** (`UseSsl`, `ServerCertificate`, `SslTargetHost`, `ServerCertificateValidationCallback`), **dispatch/send tuning** (`TcpNoDelay` Nagle toggle default-on, `MaxInFlightMessages` back-pressure, `SequentialDispatch` ordered dispatch, `SendBatching`/`SendBatchFlushMs` coalesced writes, `SendTimeoutMs` per-write deadline), and **production-hardening limits**: `MaxConnectionsLimit`, `MaxUdpPeers`, `MaxMessageSize` (TCP frame cap), `MaxConnectionsPerIpPerSecond` (per-IP rate limit), `MaxInboundQueue` (per-connection inbound-queue cap / OOM protection). `Validate()` is called on connect/start (and cross-checks e.g. reliable-default vs disabled UDP reliability). A `NetworkMetrics` instance (`Metrics`) exposes live counters.

- **Production hardening** (added after a readiness audit, then a two-round fix→re-audit loop): TLS over TCP via `SslStream` (`Core/Transport/Tcp/TcpTls.cs`); a resilient accept loop (`TcpListenerAdapter` skips a bad/garbage/stalled TLS handshake and continues, with a handshake timeout, instead of dying); `MaxConnectionsLimit`/`MaxUdpPeers` caps and `OnNewClient`+`StartReceive` guarded so a bad accept can't kill the loop; `MaxMessageSize` frame cap + a `length < 2` guard (slow-loris/OOM/negative-length protection); per-IP `RateLimiter` (`Core/RateLimiter.cs`, with idle-window eviction) on TCP accept + UDP handshake; back-pressure dispatch gate (`MaxInFlightMessages`, re-armed per connection generation); `SendTimeoutMs` bounds a stuck-peer write; Both-mode UDP bind tokens are TTL-swept (no leak) and the UDP `Disconnect` control packet is token-validated; client/peer teardown fires `OnDisconnected` exactly once; a throwing application logger can't crash the process; `BaseServer.ActiveConnections`; reconnect/heartbeat errors are logged. **Authentication is intentionally left to the application** (validate inside the server's `OnNewClient`/handlers); MessagePack is bumped to a non-vulnerable version. **UDP datagrams have no per-packet auth/encryption** — route confidentiality/integrity-sensitive traffic over TLS-over-TCP (or Both with reliable delivery).

- **NetworkMetrics** (`SetNet/Diagnostics/`): thread-safe counters (messages sent/received, connections accepted/rejected, reliable retransmits/acks, handshakes dropped) plus `Snapshot()` for export.

- **PeerInfo**: Wraps a peer's `ITransportConnection` (and an optional secondary `UdpConnection` in Both mode) along with its metadata (ID, config, server reference, command executor).

### 5. **Utilities** (`SetNet/Utils/`)

- **GameLoopScheduler**: Runs repeating tasks at fixed intervals within a game loop. Useful for server tick updates and scheduled operations. Supports background or blocking execution.

- **UpdateScheduler**: (if present) Additional scheduling utility for update-driven architectures.

- **TimerScheduler** (`SetNet/Core/`): one process-wide periodic-callback loop that drives all heartbeats and reliability ticks (instead of per-connection `Task.Delay` loops).

### 6. **Event System** (`SetNet/Events/`)

- **EventManager**: In-process pub/sub event system for decoupled communication (single-threaded; not the network layer).

## Key Patterns and Usage

### Adding a New Message Handler

1. Create a message type enum:
   ```csharp
   public enum MessageTypes : ushort { PlayerMove = 1, /* ... */ }
   ```

2. Create a `[MessagePackObject]` data class for the message.

3. Implement a handler decorated with `MessageHandlerAttribute`:

   **Server-side:**
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

   **Client-side:**
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

```csharp
await SendAsync<MyMessage>((ushort)MessageTypes.MyMessage, new MyMessage { /* ... */ });          // DefaultDelivery
await SendAsync(type, msg, DeliveryMethod.Unreliable);                                              // explicit channel
await SendAsync(type, msg, DeliveryMethod.Reliable, channel: 1);                                    // reliable-UDP channel 1
```

### Transport Selection (TCP / UDP / Both)

Choose the transport via `Configuration.TransportType` (default `Tcp`, so existing TCP code is unchanged).

| TransportType | DeliveryMethod | Carried over |
|---|---|---|
| Tcp  | any | TCP |
| Udp  | Reliable | UDP reliability layer (requires `UdpReliabilityEnabled`, else `Validate()`/send throws) |
| Udp  | Unreliable | UDP raw datagram |
| Both | Reliable | TCP |
| Both | Unreliable | UDP (falls back to TCP until/if the UDP channel attaches) |

Notes:
- **UDP is an emulated connection**: a handshake assigns peer identity and heartbeat detects liveness, so `OnConnected`/`OnDisconnected`/`BasePeer` work the same as TCP.
- **Both mode** connects TCP first, the server hands the client a UDP bind token over TCP, and the client's UDP handshake binds to the same server-side peer. If UDP is unavailable, the client degrades gracefully to TCP-only.
- **MTU**: oversize datagrams (> `UdpMaxDatagramPayload`, default 1200B) are rejected; there is no UDP fragmentation.
- **Heartbeat** in Both mode runs over the TCP lifeline; in UDP-only mode pings/pongs are unreliable datagrams.

### Disconnect / Reconnect lifecycle

`BaseClient` distinguishes intentional `Disconnect()` from unexpected loss; `OnDisconnected()` fires exactly once per connection.

| Event | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| Client `Disconnect()` (intentional) | ❌ | ❌ | ✅ | ❌ |
| Network error / server crash | ✅ | ✅ | ✅ (if reconnect fails) | ✅ (if enabled) |
| Server graceful close (bytesRead==0) | ❌ | ❌ | ✅ | ❌ |

Server-side, `BasePeer` mirrors this: `Close()` (kick) → only `OnDisconnected`; client crash/IO error → `OnError` + `OnUnexpectedDisconnect` + `OnDisconnected`.

## Project Structure

- **SetNet/**: Core library
  - `Core/`: BaseSocket, BaseClient, BaseServer, BasePeer, PacketBuilder, RateLimiter, TimerScheduler, MonotonicClock, SystemMessageTypes, Commands (CommandExecutor)
  - `Core/Transport/`: transport abstraction + enums (`TransportType`, `DeliveryMethod`); `Tcp/`, `Udp/` (handshake, demux, `ReliabilityChannel`/`ReliabilityChannelSet`), `Both/` implementations; `TransportFactory`
  - `Config/`: Configuration, PeerInfo
  - `Data/`: Handler interfaces, MessageHandlerAttribute
  - `Messaging/`: MessageProcessor, ISerializer + SetNetSerializer (pluggable serialization seam; core bundles no serializer — the MessagePack adapter `MessagePackNetSerializer` is in the separate `SetNet.MessagePack` project)
  - `Events/`: EventManager
  - `Logging/`: ILogger, ConsoleLogger, NoOpLogger
  - `Diagnostics/`: NetworkMetrics
  - `Utils/`: GameLoopScheduler, UpdateScheduler

- **SetNet.Tests/**: Manual in-process scenario harness — `dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>`

- **SetNet.UnitTests/**: xUnit unit + integration test project (`dotnet test`). Unit: PacketBuilder, UdpDatagram, AsyncQueue, MonotonicClock, Configuration.Validate, MessageProcessor, CommandExecutor, ReliabilityChannel. `Integration/`: end-to-end TCP/UDP/loss/Both round-trips, hardening (limits, rate-limit, back-pressure, TLS, batching, lifecycle), fuzz. The library exposes internals via `[InternalsVisibleTo]`.

- **examples/**: Runnable chat example split into separate processes (`Chat.Shared`, `Chat.Server`, `Chat.Client`).

## Debugging Tips

- Message handlers are auto-registered via reflection. If a handler isn't called, verify: it implements `IServerMessageHandler<T>`/`IClientMessageHandler<T>`; it's decorated with `MessageHandlerAttribute`; both the message-type (ushort) and `T` match; and the handler is in a loaded assembly.
- Connection issues often stem from Configuration mismatches (host/port). Verify both ends.
- If processing seems out of order, remember dispatch is not ordered by default — enable `SequentialDispatch`.
- `PacketBuilder` handles incomplete packets across buffer boundaries. If messages seem corrupted, check that the message type and serialization are consistent.
