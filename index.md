# SetNet

SetNet is a lightweight and efficient TCP-based networking library for .NET applications. Built on .NET Standard 2.1, it provides an easy-to-use framework for creating networked client-server applications with built-in message handling and serialization.

## Features

- 🚀 **Simple API** - Easy-to-understand base classes for creating servers and clients
- 📦 **Message Serialization** - Built-in MessagePack serialization for efficient data transfer
- 🔄 **Asynchronous Operations** - Fully async/await support for non-blocking I/O
- 🎯 **Message Handlers** - Attribute-based message handler registration
- 🔌 **TCP Protocol** - Reliable TCP-based communication
- ⚙️ **Configurable** - Flexible configuration options for host, port, buffer size, and more
- 🧩 **Extensible** - Abstract base classes for custom implementations

## Installation

Add the SetNet project to your solution or reference the compiled DLL.

### Dependencies

- .NET Standard 2.1
- MessagePack (3.1.3)

## Quick Start

### 1. Define Your Messages

Messages must be serializable with MessagePack:

```csharp
using MessagePack;

[MessagePackObject]
public class ChatMessage
{
    [Key(0)]
    public string Username { get; set; }
    
    [Key(1)]
    public string Content { get; set; }
}
```

### 2. Create Message Handlers

#### Server-side Handler

```csharp
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;

[MessageHandler(1)] // Message type ID
public class ChatMessageHandler : IServerMessageHandler
{
    public Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine($"{message.Username}: {message.Content}");
        
        // Echo back to client
        var response = new ChatMessage 
        { 
            Username = "Server", 
            Content = $"Received: {message.Content}" 
        };
        
        return Task.CompletedTask;
    }
}
```

#### Client-side Handler

```csharp
using SetNet.Data;
using SetNet.Data.Attributes;

[MessageHandler(1)]
public class ChatResponseHandler : IClientMessageHandler
{
    public Task HandleAsync(byte[] data)
    {
        var message = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine($"Server response: {message.Content}");
        
        return Task.CompletedTask;
    }
}
```

### 3. Implement the Server

```csharp
using SetNet.Config;
using SetNet.Core;

public class ChatServer : BaseServer
{
    public ChatServer(Configuration config) : base(config)
    {
    }

    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new ChatPeer(peerInfo);
        peer.StartReceive();
        
        Console.WriteLine($"New client connected: {peerInfo.Id}");
        
        return peer;
    }
}

public class ChatPeer : BasePeer
{
    public ChatPeer(PeerInfo currentPeerInfo) : base(currentPeerInfo)
    {
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine($"Client {CurrentPeerInfo.Id} disconnected.");
    }
}
```

### 4. Implement the Client

```csharp
using SetNet.Config;
using SetNet.Core;

public class ChatClient : BaseClient
{
    public ChatClient(Configuration config) : base(config)
    {
    }

    protected override void OnConnected()
    {
        Console.WriteLine("Connected to chat server!");
        
        // Send initial message
        var message = new ChatMessage 
        { 
            Username = "User123", 
            Content = "Hello, Server!" 
        };
        SendAsync(1, message);
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine("Disconnected from server.");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"Error: {error}");
    }
    
    public void SendMessage(string content)
    {
        var message = new ChatMessage 
        { 
            Username = "User123", 
            Content = content 
        };
        SendAsync(1, message);
    }
}
```

### 5. Run the Server and Client

```csharp
// Server
var serverConfig = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682,
    BufferSize = 4096,
    MaxConnections = 100
};

var server = new ChatServer(serverConfig);
await server.StartAsync();

// Client
var clientConfig = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682
};

var client = new ChatClient(clientConfig);
await client.ConnectAsync();

// Send messages
client.SendMessage("Hello from client!");

// Cleanup
client.Disconnect();
await server.StopAsync();
```

## Configuration Options

The `Configuration` class provides several options:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | `string` | - | Server IP address or hostname |
| `Port` | `int` | - | Port number for connections |
| `BufferSize` | `int` | 4096 | Size of the receive buffer in bytes |
| `MaxConnections` | `int` | 100 | Maximum number of concurrent connections |
| `UseSsl` | `bool` | false | Enable SSL/TLS encryption (future feature) |

## Message Handler Attributes

Use the `[MessageHandler(messageTypeId)]` attribute to automatically register message handlers. The message type ID must be unique and should match between client and server.

```csharp
[MessageHandler(0)] // Handles message type 0
public class LoginHandler : IServerMessageHandler
{
    public Task HandleAsync(BasePeer peer, byte[] data)
    {
        // Handle login message
        return Task.CompletedTask;
    }
}
```

## Architecture

SetNet follows a clean architecture with the following core components:

- **BaseServer** - Abstract server implementation with client management
- **BaseClient** - Abstract client implementation with connection handling
- **BasePeer** - Represents a connected peer (client) on the server
- **BaseSocket** - Base class with message handling capabilities
- **PacketBuilder** - Handles packet framing and reassembly
- **MessagePackSerializer** - Serialization utilities for messages
- **CommandExecutor** - Manages message handler registration and execution

## Packet Format

SetNet uses a simple packet format:

```
[4 bytes: Packet Length][2 bytes: Message Type][N bytes: Payload]
```

This format ensures reliable message framing over TCP streams.

## Advanced Usage

### Custom Message Registration

If you prefer manual registration instead of attributes:

```csharp
protected override void RegisterDataHandlers()
{
    base.RegisterDataHandlers();
    RegisterDataHandler(100, OnCustomMessage);
}

private async Task OnCustomMessage(byte[] data)
{
    // Handle custom message
}
```

### Broadcasting to All Clients

Extend `BaseServer` to add broadcasting functionality:

```csharp
public class ChatServer : BaseServer
{
    private readonly Dictionary<Guid, BasePeer> _clients = new();

    public void BroadcastMessage<T>(ushort messageType, T message)
    {
        foreach (var client in _clients.Values)
        {
            client.SendAsync(messageType, message);
        }
    }
}
```

## Examples

Check out the `SetNet.Tests` project for complete working examples including:

- Basic server and client setup
- Message handler implementations
- Connection management
- Data serialization

## Requirements

- .NET Standard 2.1 or higher
- Compatible with .NET Core 3.0+, .NET 5+, .NET 6+, .NET 7+, .NET 8+

## License

This project is provided as-is. Check the repository for license information.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Support

For questions and support, please open an issue on the GitHub repository.
