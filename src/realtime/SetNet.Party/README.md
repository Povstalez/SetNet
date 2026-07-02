<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Party

**Party / group system for [SetNet](https://www.nuget.org/packages/SetNet).**

A **party** is a small persistent group of friends who stick together across lobbies and matches. Unlike a [room](https://www.nuget.org/packages/SetNet.Rooms) (which is a game session), a party lives *before* and *between* games: you invite friends by code, see who's **ready**, and then enter matchmaking or a room **as a group**.

This package gives you:

- create / join a party by a short **code**,
- a **leader** (the creator; auto-promoted if they leave),
- per-member **ready** state,
- events for join / leave / leader-changed / ready-changed / disbanded.

Added by **composition** — no base class, works alongside your regular messages.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Party
```

## Setup

```csharp
PartyRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

```csharp
server.UseParties();       // that's it — parties are node-local, no store needed
```

## Client

```csharp
var party = client.UseParty();

party.PlayerJoined  += id => Log($"{id} joined");
party.PlayerLeft    += id => Log($"{id} left");
party.LeaderChanged += id => Log($"{id} is now leader");
party.ReadyChanged  += (id, ready) => UpdateReadyIcon(id, ready);
party.Disbanded     += () => ReturnToMainMenu();

// create a party (you become the leader):
var info = await party.CreateAsync();
Share(info.Code);                       // e.g. "K7Q2MZ" — send to friends

// a friend joins:
var joined = await party.JoinAsync("K7Q2MZ");
foreach (var m in joined.Members)
    Log($"{m.PlayerId} ready={m.Ready}");

// ready up / leave:
await party.SetReadyAsync(true);
await party.LeaveAsync();
```

## Entering matchmaking as a group

The leader waits until everyone is ready, then enqueues the whole party into [`SetNet.Matchmaking`](https://www.nuget.org/packages/SetNet.Matchmaking) with a shared queue key so members land in the same match:

```csharp
party.ReadyChanged += (_, _) =>
{
    var p = party; // snapshot
    if (IsLeader && p != null && AllReady(p))
        foreach (var member in p.Members)
            /* each client calls */ matchmaking.FindMatchAsync(new MatchRequest { Queue = "ranked" });
};
```

*(Party keeps the group together; Matchmaking places them; Rooms is the resulting session. The three compose.)*

## API

**Client — `PartyClient` (`client.UseParty()`):**

| Member | Purpose |
|---|---|
| `Task<PartyInfo> CreateAsync()` | create a party, become leader |
| `Task<PartyInfo> JoinAsync(code)` | join by code (throws `PartyException` if missing) |
| `Task LeaveAsync()` | leave the current party |
| `Task<PartyInfo> SetReadyAsync(bool)` | set your ready flag |
| `string? CurrentCode` | current party code, or null |
| events | `PlayerJoined(id)`, `PlayerLeft(id)`, `LeaderChanged(id)`, `ReadyChanged(id, ready)`, `Disbanded()` |

**`PartyInfo`**: `Code`, `OwnPlayerId`, `LeaderId`, `IReadOnlyList<PartyMember> Members`.
**`PartyMember`**: `PlayerId`, `Ready`.

**Server — `server.UseParties()`** installs the handler + auto-removes members on disconnect.

## Notes

- **Leader promotion**: the leader is the first member; if they leave or disconnect, the next member becomes leader and everyone gets `LeaderChanged`. An empty party is dropped.
- **Codes** are 6 chars from an unambiguous alphabet (no `O/0/I/1`), collision-checked.
- **Node-local** (parties are live peers on one server node), like Rooms. A multi-node deployment would coordinate through a shared store.
- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Party` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
