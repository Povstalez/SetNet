<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Redis

**Redis backplane for [SetNet](https://www.nuget.org/packages/SetNet) — share state across a cluster of server nodes.**

SetNet's sessions, bans and room codes live in pluggable stores that default to in-process memory: fine for one server, but they vanish on restart and aren't shared between nodes. This package provides Redis-backed implementations of all three, so when you run **several** SetNet servers behind a load balancer, they see the same authentication sessions, the same ban list, and the same room codes:

| Store | Interface | Plug into |
|---|---|---|
| `RedisSessionStore` | `ISessionStore` | `AuthOptions.SessionStore` ([SetNet.Auth](https://www.nuget.org/packages/SetNet.Auth)) |
| `RedisBanStore` | `IBanStore` | `server.UseBanList(store)` ([SetNet.BanList](https://www.nuget.org/packages/SetNet.BanList)) |
| `RedisRoomStore` | `IRoomStore` | `server.UseRooms(store)` / `server.UseMatchmaking(store, …)` ([SetNet.Rooms](https://www.nuget.org/packages/SetNet.Rooms)) |

Built on [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Redis
```

## Usage

Open one connection and share it across the stores:

```csharp
using SetNet.Redis;
using StackExchange.Redis;

var redis = RedisStores.Connect("localhost:6379");   // or ConnectionMultiplexer.Connect(...)

// Sessions resume on any node and survive restarts:
server.UseAuth(myAuthenticator, new AuthOptions
{
    SessionTtl  = TimeSpan.FromMinutes(10),
    SessionStore = new RedisSessionStore(redis, TimeSpan.FromMinutes(10)),
});

// Bans shared across all nodes (timed bans auto-expire via Redis TTL):
var bans = server.UseBanList(new RedisBanStore(redis));
bans.Ban("203.0.113.7", DateTime.UtcNow.AddHours(1));

// Room codes are unique cluster-wide:
server.UseRooms(new RedisRoomStore(redis));
server.UseMatchmaking(new RedisRoomStore(redis), new MatchmakingOptions { /* ... */ });
```

Every store takes an optional `prefix` (default `"setnet:"`) so multiple apps can share one Redis instance without key collisions.

## API

| Type | Constructor | Notes |
|---|---|---|
| `RedisStores.Connect(string)` | — | convenience wrapper over `ConnectionMultiplexer.Connect` |
| `RedisSessionStore` | `(IConnectionMultiplexer mux, TimeSpan ttl = default, string prefix = "setnet:")` | TTL default 2 min; token rotation preserved |
| `RedisBanStore` | `(IConnectionMultiplexer mux, string prefix = "setnet:")` | timed bans use Redis key TTL |
| `RedisRoomStore` | `(IConnectionMultiplexer mux, string prefix = "setnet:")` | atomically reserves unique join codes |

## Notes

- **Sessions:** persisted with a TTL, so a client can resume on **any** node and after a restart; `SweepAsync` is a no-op (Redis expires idle sessions itself). `Session.LivePeer` is a live-connection reference and is **not** persisted — so the `KickExisting` multi-session policy can only kick a session whose connection is on the *current* node. For cross-node kicks, publish a "kick" over [`SetNet.Cluster`](https://www.nuget.org/packages/SetNet.Cluster).
- **Bans:** fully shared; permanent bans are plain keys, timed bans use Redis expiry.
- **Rooms:** this store shares room **codes/metadata**, so matchmaking on any node creates codes others can look up — but a `Room`'s live members are per-node connections and are **not** stored in Redis. Actual room traffic/broadcast stays node-local (route a room's players to one node, or bridge nodes with `SetNet.Cluster`). This is the same node-local model documented for `SetNet.Rooms`.
- **One multiplexer.** `IConnectionMultiplexer` is designed to be shared and thread-safe — create it once and pass it to all three stores.
- **Security.** Protect Redis (auth/TLS/private network); it holds session tokens and bans.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
