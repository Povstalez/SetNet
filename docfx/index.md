---
_layout: landing
---

# SetNet

**A modular .NET networking library for games — server-authoritative communication over TCP, UDP or both, with a large
catalog of opt-in companion packages (rooms, matchmaking, inventory, combat, mobs, pathfinding, StateSync, and more).**

This site has three parts:

- **[Guide](articles/guide.md)** — how SetNet works: transports, handlers, the unified protocol, lifecycle, production hardening.
- **[Modules](modules/index.md)** — every companion package, with its own README (what it does + usage).
- **[API Reference](api/index.md)** — the full, generated reference for **every public type, property and method** of every
  package — pulled straight from the source XML docs, so each variable and option is documented.

## Quick start

```csharp
// pick a serializer once
SetNetSerializer.Use(new MessagePackNetSerializer());

// a server
var server = new MyServer(new Configuration { Host = "0.0.0.0", Port = 5000, TransportType = TransportType.Both });
await server.StartAsync();

// a client
var client = new MyClient(new Configuration { Host = "127.0.0.1", Port = 5000 });
await client.ConnectAsync();
await client.SendAsync((ushort)Msg.Hello, new Hello { Text = "hi" });
```

Everything above the transport — handlers, RPC, rooms, StateSync, the whole game stack — is added by **composition**:
`XxxRuntime.Enable()` + `server.UseXxx()` / `client.UseXxx()`. Pull only what you need.

## Where to go next

- New here? Read the **[Guide](articles/)**.
- Looking for a feature? Scan the **[Modules](modules/)** catalog or the "which package do I need?" table.
- Need exact signatures / every option? The **[API Reference](api/)** documents each member.
