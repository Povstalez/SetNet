<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.GeoBlock

**Reject connections by country of origin for [SetNet](https://www.nuget.org/packages/SetNet).**

Filter incoming connections by the peer's geographic location — a **blocklist** ("everyone except these countries") or an **allowlist** ("only these countries"). Blocked peers are kicked the moment they connect, before any application frame is processed. The GeoIP lookup is **pluggable** via `IGeoResolver`, so you back it with whatever you already use — MaxMind GeoLite2, IP2Location, an HTTP GeoIP API, or a static map for tests. This package ships **no** database. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.GeoBlock
```

## Usage

### Block specific countries

```csharp
var geo = server.UseGeoBlock(myResolver, new GeoBlockOptions
{
    Policy = GeoPolicy.Blocklist,
    Countries = new[] { "KP", "RU" },   // ISO 3166-1 alpha-2, case-insensitive
});

geo.Blocked += (peer, country) =>
    Console.WriteLine($"blocked {peer.RemoteEndPoint} ({country ?? "unknown"})");
```

### Allow only specific countries

```csharp
server.UseGeoBlock(myResolver, new GeoBlockOptions
{
    Policy = GeoPolicy.Allowlist,
    Countries = new[] { "UA", "PL" },
    BlockUnknown = true,   // also reject peers whose country can't be resolved
});
```

### Implementing `IGeoResolver`

```csharp
public sealed class MaxMindResolver : IGeoResolver
{
    private readonly DatabaseReader _db = new("GeoLite2-Country.mmdb");
    public string? CountryOf(IPAddress address)
        => _db.TryCountry(address, out var r) ? r?.Country?.IsoCode : null;
}
```

## API

**`server.UseGeoBlock(IGeoResolver resolver, GeoBlockOptions options)` → `GeoBlock`**

| `GeoBlockOptions` | Meaning |
|---|---|
| `GeoPolicy Policy` | `Blocklist` (reject listed) or `Allowlist` (reject unlisted). Default `Blocklist`. |
| `IReadOnlyCollection<string> Countries` | ISO alpha-2 codes the policy applies to (case-insensitive). |
| `bool BlockUnknown` | Reject peers whose country can't be resolved. Default `false` (allow). |

**`GeoBlock`**

| Member | Purpose |
|---|---|
| `event Action<BasePeer, string?> Blocked` | fired when a peer is kicked (its country, or `null` if unknown) |

**`IGeoResolver`** — `string? CountryOf(IPAddress address)` (return `null` when unknown).

## Notes

- **Needs a real remote endpoint.** GeoBlock reads `peer.RemoteEndPoint?.Address`; transports that don't expose one (e.g. the in-memory test transport) resolve to `null` → treated per `BlockUnknown`. TCP/UDP/WebSocket all expose the IP.
- **Ships no GeoIP data.** You supply the resolver and its database/service; keep the DB updated for accuracy.
- **Kick-on-connect, not a gate chain.** Blocked peers are disconnected immediately via `PeerConnected`; this is complementary to (and composes with) [`SetNet.BanList`](https://www.nuget.org/packages/SetNet.BanList) and [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth).
- **Geo is coarse.** VPNs/proxies defeat country filtering; use it for compliance/region-locking, not as a security boundary.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
