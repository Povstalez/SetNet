<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.WebSockets

**WebSocket transport for [SetNet](https://www.nuget.org/packages/SetNet).**

Run SetNet over **WebSockets** instead of raw TCP/UDP: HTTP-friendly, proxy- and firewall-traversable, and reachable from browser/Blazor and locked-down networks. Everything above the transport — handlers, RPC, rooms, auth — works **unchanged**.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.WebSockets
```

## Usage

Just switch the transport on your configuration:

```csharp
using SetNet.WebSockets;

var config = new Configuration { Host = "127.0.0.1", Port = 5000 }.UseWebSockets();

// server listens on ws://host:port/ (HttpListener under the hood)
await new MyServer(config).StartAsync();

// client connects over ws://
var client = new MyClient(config);
await client.ConnectAsync();
```

`UseWebSockets()` sets `TransportType.Custom` + a `WebSocketTransport` (via the core `ITransportProvider` hook). No other code changes.

## How it works

- Each SetNet message is one **binary** WebSocket message framed as `[2-byte type][payload]` (WebSocket message boundaries replace TCP's length prefix).
- Server side uses `HttpListener` and upgrades HTTP requests to WebSockets; a non-WebSocket request is rejected with 400 and the accept loop continues.
- WebSockets are reliable and ordered, so `DeliveryMethod`/channels are ignored (like TCP).

## Notes

- **Binding:** a specific host (e.g. `127.0.0.1`) binds without elevation; `0.0.0.0`/empty binds all interfaces via `+`, which on Windows needs a URL ACL / admin (`netsh http add urlacl`).
- **TLS (`wss://`):** put a TLS-terminating reverse proxy (nginx, Caddy, YARP, a cloud LB) in front, or bind `HttpListener` to an `https` prefix with a bound certificate. Prefer `wss` for anything over the internet.
- **Browser clients** connect with the standard JS/`System.Net.WebSockets` client; your message framing/serializer must match.
- Depends only on `SetNet`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
