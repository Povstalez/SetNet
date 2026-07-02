<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Rooms.HostMigration

**Host migration for [SetNet.Rooms](https://www.nuget.org/packages/SetNet.Rooms).**

In a room-based game one member is often the **host** — the authority that owns the match state, drives the simulation, or decides when to start. If that player leaves or drops, the room shouldn't die with them. This package adds **host migration** on top of `SetNet.Rooms`: it designates the room's creator (first member) as host and, when the host leaves or disconnects, automatically **promotes the next remaining member** and notifies everyone in the room.

You get one event — `HostChanged(roomCode, newHostPlayerId)` — and you decide what "being host" means for your game.

Added by **composition** on the same server node as your rooms; no relay, no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Rooms
dotnet add package SetNet.Rooms.HostMigration
```

## Setup

```csharp
HostMigrationRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

Host migration builds on rooms, so enable rooms first, then host migration:

```csharp
server.UseRooms();            // required — host migration hooks into the room lifecycle
server.UseHostMigration();    // designates a host per room + promotes on host loss
```

Under the hood it subscribes to the public `server.RoomHooks()` room-lifecycle events (peer joined / peer left) — no extra wiring needed.

## Client

```csharp
var rooms = client.UseRooms();
var hm    = client.UseHostMigration();

hm.HostChanged += (roomCode, newHostPlayerId) =>
{
    bool iAmHost = newHostPlayerId == rooms.CurrentRoom?.OwnPlayerId;
    if (iAmHost)
        TakeOverHostDuties();          // e.g. start driving the simulation
    else
        Log($"{newHostPlayerId} is now host of {roomCode}");
};
```

Compare `newHostPlayerId` against `rooms.CurrentRoom?.OwnPlayerId` to tell whether **you** were just promoted.

## API

**Client — `HostMigrationClient` (`client.UseHostMigration()`):**

| Member | Purpose |
|---|---|
| `event Action<string,string> HostChanged` | a room's host changed — `(roomCode, newHostPlayerId)` |

**Server — `server.UseHostMigration()`** returns a `HostMigrationServer`; installs the host-tracking + promotion logic. Requires `server.UseRooms(...)`.

## Notes

- **First joiner is host.** The creator of a room is its initial host. There's no explicit "claim host" call — it's implied by join order.
- **Promotion picks the next remaining member.** When the host leaves or disconnects, the first of the remaining members becomes host and every remaining member receives `HostChanged`. If a non-host leaves, nothing is emitted. When the last member leaves, the room's host state is discarded.
- **Reliable delivery.** The host-changed notification rides `DeliveryMethod.Reliable`.
- **Node-local**, matching Rooms — hosts and members are live peers on one server node. A multi-node deployment would coordinate through a shared store.
- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.HostMigration` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
