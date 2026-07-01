<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.DdosGuard

**Connection-flood protection for [SetNet](https://www.nuget.org/packages/SetNet).**

Counts new connections per source IP over a sliding window and **temporarily auto-bans** any IP that opens too many too fast — which drops its traffic and kicks its live peers through [`SetNet.BanList`](https://www.nuget.org/packages/SetNet.BanList). Use it to shut down application-level connection floods and abusive reconnect loops that slip past the core per-IP *accept* rate limiter: DdosGuard acts on **established** peers (via the `BaseServer.PeerConnected` event) and remembers the offender with a timed ban.

Added by **composition** — one call, no base class. It builds on `SetNet.BanList` internally and hands you back the same `BanList` so you can add manual bans or share the store.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.DdosGuard
```

## Usage

```csharp
var bans = server.UseDdosGuard(new DdosGuardOptions
{
    MaxConnectionsPerWindow = 10,   // more than 10 new connections…
    WindowSeconds           = 10,   // …from one IP within 10 seconds →
    BanSeconds              = 300,   // auto-ban that IP for 5 minutes
});

// UseDdosGuard returns the underlying BanList — manual bans, checks, shared store:
bans.Ban("203.0.113.7");
bans.Unban("203.0.113.7");
if (bans.IsBanned(someIp)) { /* ... */ }
```

Share auto-bans across nodes / restarts by passing an `IBanStore`:

```csharp
var bans = server.UseDdosGuard(new DdosGuardOptions { BanSeconds = 600 }, new RedisBanStore());
```

`UseDdosGuard()` with no arguments uses the defaults below.

## Options

| Option | Default | Meaning |
|---|---|---|
| `MaxConnectionsPerWindow` | `10` | new connections from one IP allowed within the window before it's banned |
| `WindowSeconds` | `10` | length of the sliding window, in seconds |
| `BanSeconds` | `300` | how long (seconds) an offending IP stays auto-banned |

The per-IP counter uses a monotonic clock (`Stopwatch`) and resets once the window elapses. When the count exceeds the threshold, the IP gets a timed ban expiring `BanSeconds` from now — which routes through the `BanList` gate to drop its frames and immediately kick its connected peers.

## Notes

- **Mitigation, not a shield.** This is application-level protection. It does **not** replace edge/network DDoS defense — a real volumetric attack must be absorbed upstream (cloud scrubbing, provider-level rate limits, a load balancer). DdosGuard stops abusive *connection* patterns that reach your process.
- **Needs a real remote endpoint.** Detection keys on `peer.RemoteEndPoint?.Address`; peers without one (e.g. the in-memory transport) are ignored. Works on real TCP/UDP/WebSocket transports.
- **Shared NAT / proxy caveat.** Because it bans by IP, many legitimate users behind one NAT or a reverse proxy count against the same bucket. Tune the window/threshold accordingly, and prefer authentication + account bans for targeted abuse.
- **Composes with Auth / RateLimit.** The ban gate chains onto the server's existing `InboundAuthorizer`, so DdosGuard, `SetNet.Auth`, and `SetNet.RateLimit` stack without conflict. Best paired with TLS.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
