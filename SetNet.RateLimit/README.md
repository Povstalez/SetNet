<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.RateLimit

**Per-peer inbound rate limiting for [SetNet](https://www.nuget.org/packages/SetNet) — by composition, no base class.**

Caps how fast any one peer can send application frames. Traffic over the budget is **dropped before dispatch**, protecting your handlers from floods/abuse. Uses a token bucket (sustained rate + burst).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.RateLimit
```

## Usage

```csharp
using SetNet.RateLimit;

var server = new MyServer(config);
server.UseRateLimit(new RateLimitOptions
{
    PerPeerPerSecond = 50,   // sustained frames/sec per peer
    Burst            = 100   // allowed back-to-back before throttling
});
await server.StartAsync();
```

That's it — no per-peer setup, no handler changes. Over-budget frames from a peer are silently dropped; its bucket refills over time.

## Notes

- **Composes with other gates.** If you also use `SetNet.Auth`, both gates apply — a frame must pass rate limiting **and** be authenticated. (Both wrap the core `BaseServer.InboundAuthorizer`.)
- System frames (heartbeat) are never rate-limited.
- Per-peer buckets are held weakly and clear automatically when a peer disconnects.
- Depends only on `SetNet`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
