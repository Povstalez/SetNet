<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.InMemory

**In-process loopback transport for [SetNet](https://www.nuget.org/packages/SetNet).**

A client and a server that share the same `Host:Port` connect to each other **entirely in memory** — no sockets, no OS networking, no ports actually bound — while everything above the transport (handlers, RPC, rooms, auth) behaves exactly as it would over TCP.

Use it for:
- **Fast, deterministic integration tests** — no port juggling, no `Task.Delay` waiting for the OS, no flaky "address already in use".
- **Co-hosting a client and server in one process** — single-player-as-host, editor tooling, demos.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.InMemory
```

## Usage

Call `UseInMemory()` on **both** configs and use the same `Host`/`Port` so they rendezvous. Start the server first:

```csharp
using SetNet.InMemory;

var serverConfig = new Configuration { Host = "loopback", Port = 1 }.UseInMemory();
await new MyServer(serverConfig).StartAsync();          // registers the in-memory listener

var clientConfig = new Configuration { Host = "loopback", Port = 1 }.UseInMemory();
var client = new MyClient(clientConfig);
await client.ConnectAsync();                            // links to the server in memory
```

`UseInMemory()` sets `TransportType.Custom` + an `InMemoryTransport` (via the core `ITransportProvider` hook). No other code changes — swap it back to TCP for production by removing the two calls.

`Host`/`Port` here are just a **rendezvous key**, not a real endpoint — any matching pair of strings/ints works (e.g. `"loopback":1`). Give different servers different keys to run several isolated in-memory servers in the same process (perfect for parallel test collections).

## How it works

- Each connection is one end of a linked pair; a send on one end enqueues a whole `TransportMessage` into the other end's inbound queue.
- Delivery is **reliable and ordered** (like TCP), so `DeliveryMethod`/channels are ignored.
- Payloads are **copied on send**, so the two ends never share a mutable buffer — a test can't accidentally pass by reference.
- Closing either end signals EOF to the other, so `OnDisconnected` fires just like a real socket close.

## Notes

- The **server must be started before the client connects** (the listener registers on `Start()`); connecting with no listener bound throws `IOException`.
- Binding two listeners to the same key throws — give co-located servers distinct keys.
- Reliable/ordered only — there is no simulated packet loss or latency (use the real UDP transport's loss scenarios for that).
- Depends only on `SetNet`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
