# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SetNet is a .NET networking library for client-server communication over **TCP, UDP, or both at once**. It provides a framework for building networked applications with automatic message handler registration, pluggable serialization (no serializer is bundled — MessagePack is available via the **SetNet.MessagePack** companion package, or supply your own `ISerializer`), and utilities for task scheduling.

The transport is pluggable behind a thin abstraction in `SetNet/Core/Transport/`: `ITransportConnection` (a framed message channel to one peer), `ITransportConnector` (client dialer), and `ITransportListener` (server acceptor). `BaseSocket`/`BaseClient`/`BasePeer`/`BaseServer` are transport-agnostic; everything above the transport (`MessageProcessor`, `CommandExecutor`, handler interfaces, MessagePack, heartbeat, lifecycle hooks) is shared by all transports. Select the transport with `Configuration.TransportType` (`Tcp` | `Udp` | `Both`, default `Tcp`).

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

- **BaseSocket**: Foundation class providing message-processor integration. Holds an `ITransportConnection` (the transport-agnostic channel) and routes received frames to handlers via `HandleMessage`.

- **Transport abstraction** (`SetNet/Core/Transport/`): `ITransportConnection`/`ITransportConnector`/`ITransportListener` plus implementations in `Tcp/`, `Udp/`, and `Both/`. TCP wraps `NetworkStream` + length-prefix reassembly (reuses `PacketBuilder`). UDP wraps a datagram socket with an emulated-connection handshake, heartbeat, peer demux by endpoint, and an optional `ReliabilityChannel` (sequence/ACK/retransmit/ordered). `Both` is a composite that runs TCP and UDP together. `TransportFactory` builds the right pieces from `Configuration`.
  
- **BaseClient**: Abstract client implementation that connects to a server, handles incoming messages, and manages the connection lifecycle. Subclasses implement `OnConnected()`, `OnDisconnected()`, and `OnError()` hooks.
  
- **BaseServer**: Abstract server implementation that listens for incoming connections and delegates client handling to abstract peer objects. Manages a pool of connected clients.
  
- **BasePeer**: Abstract server-side peer representing a connected client. Handles incoming data from that specific client and sends responses back. Manages bidirectional communication.

The flow: Server accepts connection → creates a BasePeer → peer receives messages → messages routed to handlers → handlers process and respond.

### 2. **Message Handling Framework** (`SetNet/Core/Commands/` + `SetNet/Data/`)

- **CommandExecutor<T>**: Uses reflection to auto-discover and register message handlers at startup. Looks for classes implementing `IServerMessageHandler` or `IClientMessageHandler` that are decorated with `MessageHandlerAttribute`.
  
- **MessageHandlerAttribute**: Marks a handler class and specifies its message type (ushort). Used by CommandExecutor for reflection-based registration.
  
- **IServerMessageHandler**: Interface for handlers that process messages on the server side. Signature: `Task HandleAsync(BasePeer peer, byte[] data)`.
  
- **IClientMessageHandler**: Interface for handlers that process messages on the client side. Signature: `Task HandleAsync(byte[] data)`.

Message handlers are discovered and instantiated automatically via reflection in the CommandExecutor constructor.

### 3. **Serialization** (`SetNet/Messaging/`)

- **ISerializer / SetNetSerializer**: Pluggable serialization seam. The core bundles **no** serializer; `SetNetSerializer.Default` (settable once at startup) is what the send path and the `SetNetSerializer.Serialize/Deserialize` facade use, and `Configuration.Serializer` can override it per connection. The MessagePack adapter (`MessagePackNetSerializer`, `UntrustedData`-hardened) lives in the separate **SetNet.MessagePack** project/package. Until a serializer is registered, the facade throws a clear error.
  
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
   public class PlayerMoveHandler : IServerMessageHandler
   {
       public async Task HandleAsync(BasePeer peer, byte[] data)
       {
           var message = SetNetSerializer.Deserialize<PlayerMoveMessage>(data);
           // Process and respond
       }
   }
   ```

   **Client-side handler:**
   ```csharp
   [MessageHandler((ushort)MessageTypes.UpdateState)]
   public class UpdateStateHandler : IClientMessageHandler
   {
       public async Task HandleAsync(byte[] data)
       {
           var message = SetNetSerializer.Deserialize<StateUpdateMessage>(data);
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

- **SetNet/**: Core library
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
  - `Program.cs`: scenario dispatcher — `dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>`

- **SetNet.UnitTests/**: xUnit unit + integration test project (`dotnet test`)
  - Unit: PacketBuilder (incl. fragmentation), UdpDatagram, AsyncQueue, MonotonicClock, Configuration.Validate, MessageProcessor, CommandExecutor, ReliabilityChannel (ordered/dedup)
  - `Integration/`: end-to-end TCP/UDP/loss/Both round-trips via a small echo harness. The library exposes internals to this project via `[InternalsVisibleTo]`.

- **examples/**: A runnable chat example using the library, split into separate processes
  - `Chat.Shared/`: message-type enum + MessagePack DTOs shared by both ends
  - `Chat.Server/`: `ChatServer`/`ChatPeer` + server handlers (broadcast, join) + entry point
  - `Chat.Client/`: `ChatClient` + client handlers (render broadcast/system notices) + console UI

## Debugging Tips

- Message handlers are auto-registered via reflection. If a handler isn't being called, verify:
  1. The class implements `IServerMessageHandler` or `IClientMessageHandler`
  2. It's decorated with `MessageHandlerAttribute` with the correct message type
  3. The message type (ushort) matches what's being sent
  4. The handler is in an assembly loaded by the AppDomain

- Connection issues often stem from Configuration mismatches (host/port). Verify both client and server use the same values.

- `PacketBuilder` handles incomplete packets across buffer boundaries. If messages seem corrupted, check that the message type and serialization are consistent.
