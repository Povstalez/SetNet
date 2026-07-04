# SetNet examples

Runnable sample apps. Each example lives in its own folder with three projects — `<Name>.Shared` (DTOs/contracts),
`<Name>.Server` and `<Name>.Client` (both console executables). They register the MessagePack serializer at startup
(`SetNetSerializer.Use(new MessagePackNetSerializer())`) and, where a companion package is used, call its
`XxxRuntime.Enable()` before connecting.

Most map directly onto the communication model — see **[docs/COMMUNICATION.md](../docs/COMMUNICATION.md)**.

## What each example teaches

| Example | Shows | Packages |
|---|---|---|
| **Chat** | Custom `SetNet.Protocol` channel: `[Op]` request/reply (Join) + fire-and-forget (Say) + `[Event]` push handlers + `PublishAsync` fan-out | core |
| **Rpc** | Request/reply via `client.CallAsync` (a typed alias of `RequestAsync`) + `[RpcMethod]` handlers | `SetNet.Rpc` |
| **Presence** | Build-your-own **topic pub/sub** on pure `SetNet.Protocol` (`[Op]` subscribe/publish + `On<T>` receive) — no companion module | core |
| **Rooms** | Rooms/lobbies by code, typed `On<T>` room broadcast | `SetNet.Rooms` |
| **Matchmaking** | Queue → auto-match → auto-join a room (`FindAndJoinAsync`) | `SetNet.Rooms` + `SetNet.Matchmaking` |
| **Party** | Party by code, leader + ready-up, party events | `SetNet.Party` |
| **Economy** | **Authoritative item drop** broadcast to the other players in a room (`TryRevokeAsync` + `BroadcastToRoomOfAsync`) | `SetNet.Rooms` + `SetNet.Inventory` |
| **Trade** | Two-player escrow trade (propose → offer → ready → confirm) | `SetNet.Inventory` + `SetNet.Trade` |
| **Auth** | Token auth **gate**: app frames are dropped until the client authenticates | `SetNet.Auth` |
| **FileTransfer** | Large-payload streaming with progress | `SetNet.Streams` |
| **Voice** | Opaque audio-frame relay in a channel | `SetNet.Voice` |
| **StateSync** | Server-authoritative entity replication (server bounces balls, client renders positions) | `SetNet.StateSync` |
| **Npc** | Walk into a zone, discover NPCs, interact → **capability hand-off** (`vendor:blacksmith`, `teleport:dungeon`) | `SetNet.NPC` |
| **World** | *(single project, no networking)* multi-storey **layered grid** + cross-floor A*, a **headless mob** chasing a player around a wall (no StateSync), and a pathfinder **micro-benchmark** | `SetNet.GeoData` + `SetNet.PathFinding` + `SetNet.Mobs` |
| **UnifiedMove** | *(single project)* a player **and** a mob move through **one** `SetNet.Locomotion` tick, one `Started` hook fires for both (send-the-point, L2-style); the mob chases the player in the shared system | `SetNet.Locomotion` + `SetNet.Mobs` + `SetNet.Mobs.Locomotion` |
| **MobBrains** | *(single project)* three mobs — one **BehaviorTree**, one **StateMachine**, one plain follower — all driven by **one** `SetNet.Ticks` scheduler (auto-subscribed), moving through **one** `SetNet.Locomotion`, each reaching the moving player via a `SetNet.Services` locator | `SetNet.Ticks` + `SetNet.Locomotion` + `SetNet.Mobs` + `SetNet.BehaviorTree` + `SetNet.StateMachine` + `SetNet.Services` |
| **LoginFlow** | *(single project)* the full L2-style entry: account → **login over the wire** → server list → one-time **token** → **character select** (with a custom `VipUntil` field) → enter world + a `GameData` lookup | `SetNet.LoginServer` + `SetNet.Accounts` + `SetNet.CharacterStore` + `SetNet.GameData` + `SetNet.InMemory` |
| **Durak** | *(single project)* a full game of **Дурак** played by two bots on the `SetNet.BoardGame` engine — each bot decides from **only its own `View`** (hidden hands), the engine validates every move | `SetNet.BoardGame` |

## Run commands

Open a terminal per process. Defaults are `127.0.0.1` and the port shown.

```bash
# Chat (5000) — server + N clients. SetNet.Protocol channel + [Op]/[Event].
dotnet run --project examples/Chat/Chat.Server
dotnet run --project examples/Chat/Chat.Client -- 127.0.0.1 5000 alice

# Rpc (5100) — non-interactive: client calls GetTime + Add and prints results
dotnet run --project examples/Rpc/Rpc.Server
dotnet run --project examples/Rpc/Rpc.Client

# Presence / pub-sub (5330) — server + clients, each on a topic
dotnet run --project examples/Presence/Presence.Server
dotnet run --project examples/Presence/Presence.Client -- news
dotnet run --project examples/Presence/Presence.Client -- news     # both on "news" see each other's lines

# Rooms (5001) — server + two clients (create / join by code)
dotnet run --project examples/Rooms/Rooms.Server
dotnet run --project examples/Rooms/Rooms.Client -- 127.0.0.1 5001 create      # prints a room code
dotnet run --project examples/Rooms/Rooms.Client -- 127.0.0.1 5001 <ROOMCODE>

# Matchmaking (5300) — server + TWO clients; they get matched into a room together
dotnet run --project examples/Matchmaking/Matchmaking.Server
dotnet run --project examples/Matchmaking/Matchmaking.Client
dotnet run --project examples/Matchmaking/Matchmaking.Client

# Party (5301) — server + a leader + members
dotnet run --project examples/Party/Party.Server
dotnet run --project examples/Party/Party.Client -- create
dotnet run --project examples/Party/Party.Client -- join <CODE>     # then type: ready / unready / /quit

# Economy — authoritative item drop (5200) — server + two clients in one room
dotnet run --project examples/Economy/Economy.Server
dotnet run --project examples/Economy/Economy.Client -- create      # prints a room code
dotnet run --project examples/Economy/Economy.Client -- join <CODE> # type "sword" to drop; the other sees it

# Trade (5310) — server logs each player's key; two clients trade
dotnet run --project examples/Trade/Trade.Server
dotnet run --project examples/Trade/Trade.Client                    # note this client's key from the server log
dotnet run --project examples/Trade/Trade.Client -- propose <KEY>   # then: offer <item> <count> / ready / confirm

# Auth (5311) — token gate ("letmein"); a wrong token is rejected
dotnet run --project examples/Auth/Auth.Server
dotnet run --project examples/Auth/Auth.Client
dotnet run --project examples/Auth/Auth.Client -- wrongtoken        # shows the rejection

# FileTransfer (5320) — non-interactive: client uploads a payload with progress
dotnet run --project examples/FileTransfer/FileTransfer.Server
dotnet run --project examples/FileTransfer/FileTransfer.Client

# Voice (5321) — server + two clients; what one types the other receives as an (opaque) frame
dotnet run --project examples/Voice/Voice.Server
dotnet run --project examples/Voice/Voice.Client
dotnet run --project examples/Voice/Voice.Client

# StateSync (5002) — server bounces balls, client prints interpolated positions
dotnet run --project examples/StateSync/StateSync.Server -- 127.0.0.1 5002 8
dotnet run --project examples/StateSync/StateSync.Client -- 127.0.0.1 5002

# Npc (5000) — server spawns a blacksmith + a teleporter in "town"; client interacts and follows the capability
dotnet run --project examples/Npc/Npc.Server
dotnet run --project examples/Npc/Npc.Client                        # non-interactive: prints each hand-off

# World — ONE project, no networking. GeoData (layered/multi-storey) + PathFinding + headless Mobs.
dotnet run --project examples/World                                 # runs all three demos
dotnet run --project examples/World -- floors                       # just the multi-storey grid + cross-floor A*
dotnet run --project examples/World -- chase                        # just the headless mob chase
dotnet run --project examples/World -- bench                        # just the pathfinder micro-benchmark

# UnifiedMove — ONE project, no networking. Player + mob in one SetNet.Locomotion tick + one Started hook.
dotnet run --project examples/UnifiedMove

# MobBrains — ONE project, no networking. BehaviorTree + StateMachine + follower mobs, all via ONE TickScheduler
# (auto-subscribed), moving through ONE Locomotion, each reading the player through a SetNet.Services locator.
dotnet run --project examples/MobBrains

# LoginFlow — ONE project. The full L2 entry: login (over the wire) → server list → token → character select → world.
dotnet run --project examples/LoginFlow

# Durak — ONE project. A full game of Дурак by two bots on the SetNet.BoardGame engine (hidden hands via per-player View).
dotnet run --project examples/Durak            # or: -- 42  for a specific deal
```

All examples build as part of the solution (`dotnet build SetNet.sln`).

More modules and a "which package do I need?" guide: **[docs/README.md](../docs/README.md)**.
