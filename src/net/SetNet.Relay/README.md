<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Relay

**A TURN-style relay hub for [SetNet](https://www.nuget.org/packages/SetNet).**

Some peers simply can't connect to each other directly — symmetric NATs, restrictive firewalls, mobile carriers. `SetNet.Relay` gives them a fallback: both peers connect to a shared server, one **allocates** a relay session (getting a join code), the other **joins** by code, and from then on either can push **opaque bytes** that the server forwards to the rest of the session. The relay never inspects the payload, so you can tunnel anything through it — a custom protocol, host-authoritative game traffic, even another framed stream. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Relay
```

## Usage

Call `RelayRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — just turn the hub on:

```csharp
RelayRuntime.Enable();
server.UseRelay();
```

**Host client** — allocate a session and share the code:

```csharp
RelayRuntime.Enable();
var relay = client.UseRelay();

relay.PeerJoined += id => Console.WriteLine($"peer {id} joined");
relay.PeerLeft   += id => Console.WriteLine($"peer {id} left");
relay.Received   += (from, data) => Handle(from, data);

string code = await relay.AllocateAsync(maxPeers: 4);   // give this code to the others
```

**Guest client** — join by code and exchange bytes:

```csharp
var relay = client.UseRelay();
relay.Received += (from, data) => Handle(from, data);

await relay.JoinAsync(code);
await relay.SendAsync(payload);                 // → all other members
await relay.SendToAsync(hostPeerId, payload);   // → one member
```

## API

**Server:** `server.UseRelay()` — enable the relay hub.

**Client:** `var relay = client.UseRelay()` → `RelayClient`

| Member | Purpose |
|---|---|
| `Task<string> AllocateAsync(int maxPeers = 0)` | create a session (0 = unlimited), join it, return the code |
| `Task JoinAsync(string code)` | join an existing session (throws `RelayException` if missing/full) |
| `Task LeaveAsync()` | leave the session (tolerant of a dropped connection) |
| `Task SendAsync(byte[] data, DeliveryMethod = Reliable)` | forward bytes to all other members |
| `Task SendToAsync(uint peerId, byte[] data, DeliveryMethod = Reliable)` | forward bytes to one member |
| `event Action<uint, byte[]> Received` | `(senderPeerId, opaque bytes)` for each relayed payload |
| `event Action<uint> PeerJoined` / `PeerLeft` | membership changes |
| `event Action Closed` | the session ended |
| `uint OwnId` / `string? Code` | this client's peer id / current session code |

`RelayRuntime.Enable()` — one-time bootstrap so the relay handlers are discovered.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Relay` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Opaque forwarding.** The relay copies bytes between members and never deserializes them — pick `DeliveryMethod.Unreliable` for latency-tolerant traffic (over a UDP/Both transport) or the default `Reliable`. Keep unreliable payloads under the transport's datagram limit (~1200 B on UDP).
- **Relay vs [Rooms](https://www.nuget.org/packages/SetNet.Rooms).** Rooms broadcast **typed** messages through your serializer and are built for lobby/game membership. Relay forwards **raw opaque** bytes with a per-sender id and 1:1 addressing — the right tool for NAT fallback and tunnelling. Use Rooms for gameplay; reach for Relay when peers can't connect directly.
- **Node-local sessions.** Sessions live on the relay server that created them (like Rooms/Voice); run one relay endpoint, or shard clients so both peers of a session land on the same node.
- **Secure it.** The relay carries whatever bytes it's given to anyone with the code — gate access with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) and run over TLS if the tunnelled traffic is sensitive.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
