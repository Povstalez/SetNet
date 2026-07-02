<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Zones

**Seamless player handoff between world nodes for [SetNet](https://www.nuget.org/packages/SetNet).**

A big world runs across many nodes, one per zone/region. When a player crosses a boundary, `SetNet.Zones` moves them without a re-login: the origin node **stashes** the player's carried state under a one-time token and pushes the client a *migrate here* instruction; the client reconnects to the destination node and **claims** the token to get its state back. The state travels through a pluggable `IHandoffStore` (share a Redis/DB store across nodes so the destination process can read what the origin wrote). Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Zones
```

## Usage

Call `ZonesRuntime.Enable()` once at startup on both ends. Every node shares one handoff store.

**Server node** — hand a player off when they cross into another zone:

```csharp
ZonesRuntime.Enable();
var zones = server.UseZones(sharedHandoffStore);   // same store instance/backend on every node

// game logic: player walked into the "desert" zone owned by another node
byte[] carried = SerializePlayer(player);          // position, buffs, current action…
await zones.TransferAsync(peer, new ZoneTarget("desert", "desert.game.example", 5000), carried);
```

**Client** — follow the handoff:

```csharp
ZonesRuntime.Enable();
var zones = client.UseZones();

zones.TransferRequested += async transfer =>
{
    var next = new GameClient(new Configuration { Host = transfer.Target.Host, Port = transfer.Target.Port /* ... */ });
    var nextZones = next.UseZones();
    await next.ConnectAsync();

    byte[] carried = await nextZones.ClaimAsync(transfer.Token);   // state from the origin node
    RestorePlayer(carried);                                        // spawn seamlessly in the new zone
    oldClient.Disconnect();
};
```

## API

**Server:** `server.UseZones(IHandoffStore?, ZoneOptions?)` → `ZonesServer`

| Member | Purpose |
|---|---|
| `Task<string> TransferAsync(peer, ZoneTarget, byte[] carryState)` | stash state + push a migrate instruction; returns the token |
| `KeyOf(peer)` | the peer's stable player key |

**Client:** `var zones = client.UseZones()` → `ZonesClient`

| Member | Purpose |
|---|---|
| `event Action<ZoneTransfer> TransferRequested` | server asked this client to migrate (target + token) |
| `Task<byte[]> ClaimAsync(token)` | on the destination node, retrieve the carried state |

**Store:** `IHandoffStore` (`MemoryHandoffStore` default) — `PutAsync(token, handoff, ttl)` / `TakeAsync(token)` (one-time).
**Options:** `ZoneOptions.PlayerKey`, `HandoffTtl` (60 s).

`ZonesRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Zones` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **The store must be shared for real cross-node handoff.** The default `MemoryHandoffStore` only works when origin and destination are the same process (co-located nodes / tests). For separate processes, back `IHandoffStore` with Redis/DB so the destination can read the stashed state.
- **Where's the destination node?** `SetNet.Zones` carries state; it doesn't decide *which* node owns a zone. Pair it with [`SetNet.Sharding`](https://www.nuget.org/packages/SetNet.Sharding) (`ring.GetNode(zoneId)`) or your own zone→node map to fill in `ZoneTarget.Host/Port`.
- **One-time, expiring tokens.** A token is consumed on the first successful claim and swept after `HandoffTtl`. Carry only what the destination needs to reconstruct the player; keep durable data (inventory, progression) in shared stores like [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory).
- **Trust.** The carried state is opaque and, if the store is shared, written by a trusted peer node — validate it on the destination and gate player connections with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth). Re-authenticate on the destination node like any new connection.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
