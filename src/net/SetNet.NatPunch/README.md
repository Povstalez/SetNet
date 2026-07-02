<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.NatPunch

**UDP hole-punching for [SetNet](https://www.nuget.org/packages/SetNet): open a direct peer-to-peer UDP path instead of relaying everything.**

Two clients behind home routers usually can't reach each other directly — but if both fire UDP datagrams at each other *at the same time*, most NATs open a bidirectional hole. `SetNet.NatPunch` provides the two halves of that dance: a **coordinator** on your existing SetNet server that exchanges each side's public (server-observed) and private (LAN) endpoint candidates, and a **puncher** (`NatPuncher.TryPunchAsync`) that probes every candidate simultaneously and reports the endpoint the hole opened on. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.NatPunch
```

## Usage

Call `NatPunchRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — just turn the coordinator on:

```csharp
NatPunchRuntime.Enable();
server.UseNatPunch();
```

**Host client** — register a session, share the code, then punch:

```csharp
NatPunchRuntime.Enable();
var np = client.UseNatPunch();

const int myUdpPort = 40001;                       // the port you'll punch (and later play) from
string code = await np.RegisterAsync(myUdpPort);   // give this code to the guest

NatPunchTarget guest = await np.WaitForGuestAsync();
IPEndPoint? hole = await NatPuncher.TryPunchAsync(myUdpPort, guest);
```

**Guest client** — punch by code:

```csharp
var np = client.UseNatPunch();

const int myUdpPort = 40002;
NatPunchTarget host = await np.PunchAsync(code, myUdpPort);
IPEndPoint? hole = await NatPuncher.TryPunchAsync(myUdpPort, host);

if (hole == null)
    /* fall back to SetNet.Relay */ ;
else
    /* connect your UDP traffic from myUdpPort to hole — immediately, mappings expire when idle */ ;
```

## API

**Server:** `server.UseNatPunch()` — enable the punch coordinator.

**Client:** `var np = client.UseNatPunch()` → `NatPunchClient`

| Member | Purpose |
|---|---|
| `Task<string> RegisterAsync(int udpPort)` | register a punch session as host, return the join code |
| `Task<NatPunchTarget> PunchAsync(string code, int udpPort, int timeoutMs = 10000)` | request a punch as guest; returns the host's candidates |
| `Task<NatPunchTarget> WaitForGuestAsync(int timeoutMs = 60000)` | host-side: await the next guest's candidates |
| `Task CancelAsync()` | unregister the host's session |
| `event Action<NatPunchTarget> TargetReceived` | raised per counterpart (hosts get one per guest) |

**Puncher:** `NatPuncher`

| Member | Purpose |
|---|---|
| `Task<IPEndPoint?> TryPunchAsync(int localPort, NatPunchTarget target, int timeoutMs = 5000, CancellationToken ct = default)` | probe all candidates; returns the opened endpoint or null |
| `string[] GetPrivateEndPoints(int port)` | this machine's LAN candidates (sent automatically by the driver) |

`NatPunchRuntime.Enable()` — one-time bootstrap so the handlers are discovered.

## Notes

- **Reserved wire types 65495 / 65496 / 65497.** Don't reuse them.
- **Works for the common case, not every case.** The public candidate is the server-observed source IP plus the client-reported UDP port — correct for full-cone and port-preserving NATs (typical home routers). **Symmetric NATs** (common on mobile carriers and corporate networks) randomize ports per destination and will not punch: detect the `null` result and fall back to [`SetNet.Relay`](https://www.nuget.org/packages/SetNet.Relay).
- **Punch, then use the port immediately.** NAT mappings expire within seconds when idle. Bind your real traffic (e.g. a SetNet UDP client/host) to the same local port right after a successful punch, or keep a keepalive going.
- **Same-LAN peers** connect via the private candidates automatically — the puncher probes public and private endpoints in the same sweep.
- **Both sides must punch simultaneously.** The coordinator pushes both events back-to-back for exactly that reason; start `TryPunchAsync` as soon as your target arrives.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
