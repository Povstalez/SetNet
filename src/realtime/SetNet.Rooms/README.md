<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Rooms

**Rooms & lobbies for [SetNet](https://www.nuget.org/packages/SetNet) — by composition, no base class.**

Create and join rooms **by code**, broadcast within a room, and get **player-joined / left / message** events. Runs on a **dedicated server** — the server is the hub, so **no relay is needed**. A peer is auto-removed from its room when it disconnects.

## Do I need a relay?

**No.** Rooms group the clients already connected to your SetNet server; the server routes messages within a room. A *relay* is a different topology (host-authoritative P2P, à la Among Us) where the server forwards opaque frames between a host and members — that's optional and separate. For a normal dedicated server, this package is all you need.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.MessagePack   # or your own ISerializer
dotnet add package SetNet.Rooms
```

At startup (once), before constructing your client/server:

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rooms;

SetNetSerializer.Use(new MessagePackNetSerializer());
RoomsRuntime.Enable();   // ensures the room handlers are discovered
```

## Server

```csharp
var server = new MyServer(config);
server.UseRooms();                 // default in-memory room store
// server.UseRooms(new RedisRoomStore(...));   // or a custom IRoomStore
await server.StartAsync();
```

That's it — the server now handles create/join/leave/broadcast and cleans up rooms as players leave or drop.

## Client

```csharp
var client = new MyClient(config);
var rooms = client.UseRooms();
await client.ConnectAsync();

// Host creates a room and shares the code:
var room = await rooms.CreateAsync(new RoomOptions { MaxPlayers = 8 });
Console.WriteLine($"Join code: {room.Code}");   // e.g. "K7P2QX"

// Another player joins by code:
var joined = await rooms.JoinAsync("K7P2QX");   // throws RoomException if missing/full

// React to others:
rooms.PlayerJoined   += id => Console.WriteLine($"{id} joined");
rooms.PlayerLeft     += id => Console.WriteLine($"{id} left");
rooms.MessageReceived += (from, payload) =>
{
    var move = SetNetSerializer.Deserialize<MoveMessage>(payload);
    // apply move from `from`
};

// Broadcast to everyone else in the room:
await rooms.BroadcastAsync(new MoveMessage { X = 1, Y = 2 });

await rooms.LeaveAsync();
```

- `rooms.CurrentRoom` gives the code, your player id, and the member list.
- `BroadcastAsync<T>(msg)` serializes with your serializer; `MessageReceived` hands you the raw payload to deserialize (so the server never touches your game types).

## Pluggable store

Rooms live in an in-process `MemoryRoomStore` by default. Implement `IRoomStore` (async) and pass it to `UseRooms(...)` for custom behavior. Note: room membership is **node-local** (members are live connections on one server); a true multi-node/cluster room system needs a coordinator on top and is out of scope for v1.

## Notes

- **Serializer-agnostic, depends only on `SetNet`** — the room protocol is hand-framed (no MessagePack dependency); your broadcast bodies use your `SetNetSerializer`.
- Uses reserved wire type ids `65526`/`65527`/`65528` — don't use those for your own messages.
- **One client per process** is the norm and fully correct. Running **multiple clients in one process** (e.g. bots/tests) shares event routing (events are filtered by room code), so co-located clients in the same room both observe events — fine in practice, a caveat for in-process multi-client.
- Host designation / host migration and a relay mode are planned for a future version.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet
- 📖 [User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)

## License

MIT
