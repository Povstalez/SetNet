<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Gateway

**Front-end gateway / reverse proxy for [SetNet](https://www.nuget.org/packages/SetNet).**

`GatewayServer` accepts client connections and relays their frames **byte-for-byte** to a backend SetNet server chosen per client — and relays the backend's frames back. Because it forwards **raw** frames (via the core `OnRawFrame` / `SendRawAsync` hooks) it never deserializes payloads: no serializer, no per-message allocation of your DTOs, just a cheap pipe.

Reach for it to:

- **Shard** players across backend nodes (route by remote IP, region, or any logic you like),
- **Terminate a public transport** (e.g. WebSockets via `config.UseWebSockets()`) in front of internal TCP backends,
- add a routing / edge layer without touching your game code.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Gateway
```

## Usage

```csharp
using SetNet.Gateway;

var listenConfig = new Configuration { Host = "0.0.0.0", Port = 5000 };
// listenConfig.UseWebSockets();   // e.g. terminate ws:// at the edge

var gateway = new GatewayServer(
    listenConfig,
    backendSelector: peerInfo =>
    {
        // route this client to a backend — return that backend's Configuration:
        var node = ShardFor(peerInfo);              // your logic (IP, region, hash, ...)
        return new Configuration { Host = node.Host, Port = node.Port };
    });

await gateway.StartAsync();
```

`backendSelector` runs **once per accepted client**; the returned `Configuration` describes the backend that client will be pinned to for its whole session. Each client gets its own dedicated backend connection.

A simple round-robin / IP-hash shard:

```csharp
string[] backends = { "10.0.0.11", "10.0.0.12", "10.0.0.13" };

var gateway = new GatewayServer(listenConfig, peerInfo =>
{
    var ip = peerInfo /* inspect remote endpoint / metadata */;
    var idx = Math.Abs(ip.GetHashCode()) % backends.Length;
    return new Configuration { Host = backends[idx], Port = 6000 };
});
```

## How it works

| Direction | Path |
|---|---|
| client → gateway | `GatewayPeer.OnRawFrame` consumes every application frame (returns `true`, so no typed dispatch on the gateway) |
| gateway → backend | forwarded via the backend client's `SendRawAsync`; frames are **buffered** until the backend connection is up, then flushed in order |
| backend → gateway | the backend client's `OnRawFrame` receives frames |
| gateway → client | forwarded back with the peer's `SendRawAsync` |

Nothing is deserialized in either direction; the gateway is transport-agnostic above the frame boundary.

## Notes

- **One backend connection per client**, opened lazily. Until it's established, the client's frames are queued (in order) and flushed once connected.
- **Backend failure drops the client.** If the backend can't be reached, or the backend connection later closes, the front-end client is disconnected — the gateway does not retry or fail over on your behalf. Put reconnection/failover logic in your client or in `backendSelector`.
- **Byte-for-byte relay.** Because frames pass through unparsed, the gateway can't inspect, filter, or transform application messages. It's a pipe, not a firewall — do auth/validation on the backend (see [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth)).
- **Mixed transports are fine:** terminate WebSockets (or UDP/Both) at the edge and speak plain TCP to internal backends — pick each side's transport in the respective `Configuration`.
- **System frames** (heartbeat, handshake, etc.) are handled by the gateway's own hop; only application frames are relayed, so each hop maintains its own liveness independently.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
