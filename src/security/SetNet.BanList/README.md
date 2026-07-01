<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.BanList

**Ban enforcement for [SetNet](https://www.nuget.org/packages/SetNet).**

Drop all traffic from banned peers and kick matching live connections instantly. Ban by **IP address** (the default) or by a **custom key** such as the authenticated account id. Bans can be permanent or timed. The store is pluggable — in-process by default, or supply a Redis/DB implementation to share bans across nodes and survive restarts. Added by **composition** (no base class): the enforcement gate is *chained* onto the server's existing `InboundAuthorizer`, so it stacks cleanly with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) and [`SetNet.RateLimit`](https://www.nuget.org/packages/SetNet.RateLimit).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.BanList
```

## Usage

### Ban by IP (default)

```csharp
var bans = server.UseBanList();

bans.Ban("203.0.113.7");                                 // permanent — kicks live peers + blocks reconnects
bans.Ban("203.0.113.7", DateTime.UtcNow.AddHours(1));    // timed ban (auto-expires)
bans.Unban("203.0.113.7");

if (bans.IsBanned("203.0.113.7")) { /* ... */ }
```

`Ban` does two things: it records the ban in the store, and it immediately kicks every currently-connected peer whose key matches.

### Ban by account id

Pair with `SetNet.Auth` and key bans on the authenticated identity instead of the raw IP:

```csharp
var bans = server.UseBanList(peer => AccountOf(peer));   // your peer → account-id selector
bans.Ban("account-123");
```

### Cross-node / persistent bans

Pass your own `IBanStore` (must be thread-safe) so bans are shared and outlive a restart:

```csharp
var bans = server.UseBanList(new RedisBanStore());               // IP-keyed, shared store
var bans = server.UseBanList(peer => AccountOf(peer), redisStore); // account-keyed, shared store
```

## API

**`server.UseBanList(...)` → `BanList`**

| Overload | Ban key |
|---|---|
| `UseBanList(IBanStore? store = null)` | peer's remote IP (falls back to the peer's connection id if no `RemoteEndPoint`) |
| `UseBanList(Func<BasePeer,string> keySelector, IBanStore? store = null)` | whatever your selector returns (e.g. account id) |

**`BanList`**

| Member | Purpose |
|---|---|
| `void Ban(string key, DateTime? untilUtc = null)` | ban a key (permanent when `untilUtc` is null) and kick matching live peers |
| `void Unban(string key)` | lift a ban |
| `bool IsBanned(string key)` | current ban state (expired timed bans read as not-banned) |
| `void Kick(BasePeer peer)` | force-disconnect one peer |

**`IBanStore`** — `bool IsBanned(string)`, `void Ban(string, DateTime?)`, `void Unban(string)`. Default implementation `MemoryBanStore` is an in-process dictionary that lazily evicts expired timed bans.

## Notes

- **By-IP needs a real endpoint.** The default keys on `peer.RemoteEndPoint?.Address`; transports that don't expose one (e.g. the in-memory test transport) fall back to the connection id, which is per-connection — so an IP ban won't survive reconnect there. On real TCP/UDP/WebSocket transports the IP is available.
- **The default store is per-process.** Bans vanish on restart and aren't shared between server nodes — supply a Redis/DB `IBanStore` for a cluster or for durability.
- **Composes, doesn't replace.** The gate chains onto any prior `InboundAuthorizer`, so install order with Auth/RateLimit doesn't matter — all gates must pass for a frame to be delivered.
- **IPv4/IPv6 and shared NATs.** IP bans hit everyone behind the same NAT/proxy; prefer account-keyed bans once you have authentication.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
