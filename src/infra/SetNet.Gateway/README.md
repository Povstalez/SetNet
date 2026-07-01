# SetNet.Gateway

**Front-end gateway / reverse proxy for [SetNet](https://www.nuget.org/packages/SetNet).**

Accepts client connections and relays their frames **byte-for-byte** to a backend SetNet server chosen per client — and relays the backend's frames back. It forwards **raw** frames (never deserializing), so it needs no serializer and stays cheap.

Use it to:
- **Shard** players across backend nodes (route by IP, region, or your own logic).
- **Terminate a public transport** (e.g. WebSockets) in front of internal TCP backends.
- Add a routing/edge layer without touching game code.

```csharp
using SetNet.Gateway;

var gateway = new GatewayServer(
    listenConfig: new Configuration { Host = "0.0.0.0", Port = 5000 },   // or .UseWebSockets()
    backendSelector: peerInfo => new Configuration { Host = PickBackend(peerInfo), Port = 6000 });

await gateway.StartAsync();
```

Each client gets its own backend connection; frames are buffered until the backend connects, and a backend failure drops the client. The gateway and each hop heartbeat independently.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
