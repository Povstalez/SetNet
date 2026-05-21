# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SetNet is a .NET networking library for client-server communication using TCP sockets. It provides a framework for building networked applications with automatic message handler registration, serialization via MessagePack, and utilities for task scheduling.

## Build and Test Commands

**Build the project:**
```bash
dotnet build
```

**Run the test application:**
```bash
dotnet run --project SetNet.Tests
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

- **BaseSocket**: Foundation class providing low-level socket operations and message processor integration. Manages NetworkStream and PacketBuilder.
  
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

- **MessagePackSerializer**: Serializes/deserializes messages using the MessagePack format. Used for converting strongly-typed messages to byte arrays before transmission.
  
- **PacketBuilder**: Encodes messages into the wire protocol (prefixes with length header) and reassembles incoming data into complete packets. Handles frame boundaries.
  
- **MessageProcessor**: Routes incoming byte[] messages to registered async/sync handlers by type identifier.

### 4. **Configuration** (`SetNet/Config/`)

- **Configuration**: Holds connection settings (Host, Port, BufferSize, MaxConnections, UseSsl) and reconnection options (AutoReconnect, MaxReconnectAttempts, ReconnectDelayMs).
  
- **PeerInfo**: Wraps a connected TCP client along with its metadata (ID, config, server reference, command executor).

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
           var message = MessagePackSerializer.Deserialize<PlayerMoveMessage>(data);
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
           var message = MessagePackSerializer.Deserialize<StateUpdateMessage>(data);
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
```

## Project Structure

- **SetNet/**: Core library
  - `Core/`: BaseSocket, BaseClient, BaseServer, BasePeer, Commands (CommandExecutor)
  - `Config/`: Configuration, PeerInfo
  - `Data/`: Handler interfaces, MessageHandlerAttribute
  - `Messaging/`: PacketBuilder, MessageProcessor, MessagePackSerializer, IMessagePackFactory
  - `Events/`: EventManager
  - `Utils/`: GameLoopScheduler, UpdateScheduler

- **SetNet.Tests/**: Test application demonstrating the framework
  - `Core/`: MainServer, MainClient, PlayerPeer implementations
  - `Data/`: MessageTypes, TestMessage, UpdateClientIdMessage
  - `Messages/`: Handler implementations for test messages

## Debugging Tips

- Message handlers are auto-registered via reflection. If a handler isn't being called, verify:
  1. The class implements `IServerMessageHandler` or `IClientMessageHandler`
  2. It's decorated with `MessageHandlerAttribute` with the correct message type
  3. The message type (ushort) matches what's being sent
  4. The handler is in an assembly loaded by the AppDomain

- Connection issues often stem from Configuration mismatches (host/port). Verify both client and server use the same values.

- `PacketBuilder` handles incomplete packets across buffer boundaries. If messages seem corrupted, check that the message type and serialization are consistent.
