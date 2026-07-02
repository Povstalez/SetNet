<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Upnp

**UPnP IGD port mapping for [SetNet](https://www.nuget.org/packages/SetNet): make a player-hosted server reachable without touching the router.**

Most home routers support UPnP IGD — a LAN protocol that lets an application ask the router to forward a port. `SetNet.Upnp` discovers the gateway via SSDP and drives its WAN service: read the external IP, add a TCP/UDP port mapping, remove it on shutdown. No wire types, no server component — it's a pure client-side utility you run on whichever machine listens for connections.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Upnp
```

## Usage

**One call for a SetNet server** — map every port the configuration listens on:

```csharp
var device = await config.MapServerPortsAsync(description: "MyGame host");
if (device == null)
    Console.WriteLine("No UPnP gateway — ask the player to forward the port manually.");
else
    Console.WriteLine($"Reachable at {await device.GetExternalIpAsync()}:{config.Port}");

// on shutdown:
await device.DeletePortMappingAsync(UpnpProtocol.Tcp, config.Port);
```

**Manual control:**

```csharp
UpnpDevice? gw = await UpnpPortMapper.DiscoverAsync();
if (gw != null)
{
    IPAddress external = await gw.GetExternalIpAsync();
    await gw.AddPortMappingAsync(UpnpProtocol.Udp, externalPort: 40001, internalPort: 40001,
                                 description: "MyGame voice", leaseSeconds: 3600);
    // ... later
    await gw.DeletePortMappingAsync(UpnpProtocol.Udp, 40001);
}
```

## API

| Member | Purpose |
|---|---|
| `UpnpPortMapper.DiscoverAsync(timeoutMs = 3000)` | SSDP-discover the gateway; null when none answers |
| `UpnpPortMapper.FromLocationAsync(Uri location)` | drive a gateway with a known description URL (fixed setups, tests) |
| `device.GetExternalIpAsync()` | the router's WAN IP |
| `device.AddPortMappingAsync(protocol, externalPort, internalPort, description, leaseSeconds = 0)` | forward a port to this machine |
| `device.DeletePortMappingAsync(protocol, externalPort)` | remove a mapping |
| `device.LocalAddress` / `ControlUrl` / `ServiceType` | discovery results |
| `config.MapServerPortsAsync(description, leaseSeconds = 0)` | map the TCP/UDP ports a SetNet `Configuration` listens on |

## Notes

- **No reserved wire types** — this package never touches the SetNet connection; it talks HTTP/SOAP to the router on the LAN.
- **UPnP can be off.** Many routers ship with UPnP disabled (and some networks disable it deliberately). Always handle the `null` result — fall back to [`SetNet.NatPunch`](https://www.nuget.org/packages/SetNet.NatPunch) or [`SetNet.Relay`](https://www.nuget.org/packages/SetNet.Relay).
- **Leases.** `leaseSeconds: 0` requests a permanent mapping; a few IGDv2 routers cap or refuse that — pass an explicit lease and refresh it if you target those. Delete mappings on shutdown either way.
- **Supported services:** `WANIPConnection:1`/`:2` and `WANPPPConnection:1`.
- **Security note:** UPnP mappings expose the mapped port to the whole internet — gate the exposed server with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth)/[`SetNet.RateLimit`](https://www.nuget.org/packages/SetNet.RateLimit) like any public endpoint.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
