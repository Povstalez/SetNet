<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Matchmaking

**Matchmaking for [SetNet](https://www.nuget.org/packages/SetNet), on top of [SetNet.Rooms](https://www.nuget.org/packages/SetNet.Rooms).**

Queue players into matches — FIFO or skill-based with a **widening acceptance window** so nobody waits forever — then drop each formed match into a freshly created room they join. Added by **composition**: no base class, works alongside your regular messages.

```csharp
var match = await matchmaking.FindMatchAsync(new MatchRequest { Queue = "ranked", Skill = 1500 });
await rooms.JoinAsync(match.RoomCode);   // now you're in a room with your opponents
```

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Rooms
dotnet add package SetNet.Matchmaking
```

At startup (before constructing your client/server), enable both layers and register a serializer:

```csharp
SetNetSerializer.Use(new MessagePackNetSerializer());   // from SetNet.MessagePack
RoomsRuntime.Enable();
MatchmakingRuntime.Enable();
```

## Server

Pass the **same** `IRoomStore` to `UseRooms` and `UseMatchmaking` — matched players are placed into a room created in that store, which they then join through Rooms:

```csharp
var store = new MemoryRoomStore();               // share one store
server.UseRooms(store);
server.UseMatchmaking(store, new MatchmakingOptions
{
    MatchSize = 2,          // players per match
    UseSkill  = true,       // group by rating (omit for pure FIFO)
    TickIntervalMs = 500,
});
```

## Client

```csharp
var rooms = client.UseRooms();
var matchmaking = client.UseMatchmaking();

// Enter the queue and await a match (cancel the token to leave the queue):
var match = await matchmaking.FindMatchAsync(new MatchRequest { Queue = "ranked", Skill = 1500 });
Console.WriteLine($"Matched with {match.Players.Count} players in room {match.RoomCode}");
await rooms.JoinAsync(match.RoomCode);

// …or do both in one call:
var room = await matchmaking.FindAndJoinAsync(new MatchRequest { Queue = "ranked" }, rooms);
```

`MatchFound` also fires as an event, and `CancelAsync()` leaves the queue.

## How matching works

- Players are grouped **per queue** — only players in the same `Queue` string can match (use it for mode + region, e.g. `"ranked-eu"`).
- **FIFO** by default (`UseSkill = false`): the oldest `MatchSize` waiting players form a match.
- **Skill-based** (`UseSkill = true`): the server sorts waiting players by rating and forms a group whose skill spread fits inside every member's **acceptance window**. That window starts at `BaseSkillWindow` and grows by `SkillWindowGrowthPerSecond` for every second a player waits — so a close match is preferred early, but a long wait eventually matches anyone.
- A background ticker (`TickIntervalMs`) forms matches; when one forms, the server creates a room (capacity `MatchedRoomMaxPlayers`, default `MatchSize`) and pushes a `MatchFound` only to those players.
- A player who disconnects or cancels is removed from the queue automatically (via the core `PeerDisconnected` event).

## Options

| Option | Default | Meaning |
|---|---|---|
| `MatchSize` | `2` | Players per match |
| `UseSkill` | `false` | Skill-based grouping vs pure FIFO |
| `BaseSkillWindow` | `100` | Initial ± skill spread a group may span |
| `SkillWindowGrowthPerSecond` | `50` | How fast the window widens while waiting |
| `TickIntervalMs` | `500` | How often the matchmaker runs |
| `MatchedRoomMaxPlayers` | `0` (= `MatchSize`) | Capacity of the created room |

## Notes

- Runs on a **dedicated server** (the same node that hosts Rooms). Because rooms are node-local, matchmaking is node-local too; a multi-node deployment would coordinate through a shared `IRoomStore` implementation.
- Reserved wire types `65523/65524/65525` (just below the Rooms range) — don't reuse them.
- Depends on `SetNet` + `SetNet.Rooms`; serializer-agnostic (hand-framed `byte[]` protocol).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
