# SetNet examples

Runnable, minimal samples — each is a separate **server** and **client** console app (with a small **Shared** project for the DTOs/schema), so you run them in different terminals over TCP.

> Register a serializer and call the relevant `XxxRuntime.Enable()` once at startup — every example does this in its `Program.cs`.

## Chat — one-way messages + broadcast

Classic chat: each client sends lines, the server relays them to everyone. Shows plain `[MessageHandler]` typed handlers and server-side broadcast.

```bash
dotnet run --project examples/Chat.Server -- 127.0.0.1 5000
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 alice
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 bob
```

## Rooms — lobbies + typed `On<T>` broadcast

Players join a shared room by **code** and chat within it. Shows [`SetNet.Rooms`](../src/realtime/SetNet.Rooms/README.md): `CreateAsync`/`JoinAsync`, `PlayerJoined`/`PlayerLeft`, and the **typed** broadcast API `BroadcastAsync<T>(messageType, msg)` → `On<T>(messageType, (from, msg) => …)` (no raw bytes). The server is just `server.UseRooms()`.

```bash
dotnet run --project examples/Rooms.Server -- 127.0.0.1 5001
dotnet run --project examples/Rooms.Client -- 127.0.0.1 5001 create      # prints a room code
dotnet run --project examples/Rooms.Client -- 127.0.0.1 5001 <ROOMCODE>  # a friend joins it
```

## StateSync — server-authoritative replication

A headless server spawns balls bouncing in a cube and replicates their positions; the client prints them each frame. Shows [`SetNet.StateSync`](../src/realtime/SetNet.StateSync/README.md): a shared archetype schema, `world.Spawn(...)` + mutating fields on the server, and `EntitySpawned`/`Entities`/interpolated `GetVec3(...)` on the client.

```bash
dotnet run --project examples/StateSync.Server -- 127.0.0.1 5002 8   # 8 balls
dotnet run --project examples/StateSync.Client -- 127.0.0.1 5002
```

---

More modules, patterns, and a "which package do I need?" guide: **[docs/README.md](../docs/README.md)**.
