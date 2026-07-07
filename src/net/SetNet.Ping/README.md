# SetNet.Ping

Round-trip **latency (ping)** measurement for SetNet. Read any player's ping to the server **at any moment**, and let a client measure its own RTT.

## Server — every player's ping

```csharp
using SetNet.Ping;

var ping = server.UsePing(new PingOptions { IntervalMs = 1000 });   // pings each peer once a second
PingRuntime.Enable();

// …anywhere, any time:
double ms = ping.Of(peer);          // smoothed RTT in ms (-1 until measured)
double last = ping.LastOf(peer);    // most recent sample

ping.Updated += (peer, rtt) => { /* e.g. show it on a scoreboard */ };
```

## Client — echo + own ping

```csharp
var pc = client.UsePing();          // answers the server's pings (required for the server to measure you)
await pc.MeasureAsync();            // measure my own RTT to the server
Console.WriteLine($"my ping: {pc.Last:F0} ms");
// or auto: client.UsePing(new PingOptions { ClientAutoPingMs = 1000 });  → pc.Updated / pc.Smoothed
```

## How it works

Rides the unified protocol on `Channels.Ping`. The initiator sends a `Ping` carrying its own monotonic `Stopwatch` timestamp; the other side echoes it back as `Pong`; the initiator computes `RTT = now − stamp`. Because the timestamp round-trips on the **initiator's** clock, no clock sync is needed. Both directions use the same pair:

- The **server** pings each peer on a timer → the client echoes → `ping.Of(peer)`.
- The **client** pings the server → the server echoes → `pc.Last`.

`PingOptions`: `IntervalMs` (server), `Smoothing` (EWMA α, default 0.2), `ClientAutoPingMs`. Both ends must call `UsePing()` (the client's install answers the server's pings). Depends only on `SetNet`.

## License
MIT
