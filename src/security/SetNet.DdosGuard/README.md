<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.DdosGuard

**Connection-flood protection for [SetNet](https://www.nuget.org/packages/SetNet).**

Counts new connections per source IP over a sliding window and **temporarily auto-bans** an IP that opens too many too fast — dropping its traffic and kicking its live peers (via [`SetNet.BanList`](https://www.nuget.org/packages/SetNet.BanList)). Complements the core per-IP *accept* rate limiter by acting on already-established peers and remembering the offender.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.DdosGuard
```

```csharp
var bans = server.UseDdosGuard(new DdosGuardOptions
{
    MaxConnectionsPerWindow = 10,   // >10 connects…
    WindowSeconds = 10,             // …within 10s →
    BanSeconds = 300,               // 5-minute auto-ban
});

// returns the underlying BanList — add manual bans, share a store, etc.
bans.Ban("203.0.113.7");
```

Supply an `IBanStore` (Redis/DB) to share auto-bans across nodes. Best paired with TLS and `SetNet.Auth`.

> This is a mitigation, not a substitute for edge/network DDoS protection (a real volumetric attack should be absorbed upstream). It stops application-level connection floods and abusive reconnect loops.

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
