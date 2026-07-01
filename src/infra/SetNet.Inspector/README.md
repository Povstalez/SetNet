<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Inspector

**Built-in live metrics dashboard for a [SetNet](https://www.nuget.org/packages/SetNet) server.**

A tiny diagnostics server that runs on `HttpListener` (no ASP.NET dependency) and exposes your server's live `NetworkMetrics` and active connection count:

- `GET /metrics` → JSON snapshot (scrape it from your own tooling),
- `GET /` → a self-refreshing HTML page (polls `/metrics` every second) you can open in a browser to watch traffic in real time.

Great for local debugging, a staging node, or a quick "is it moving?" glance during a load test — with zero extra infrastructure.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inspector
```

## Usage

```csharp
using SetNet.Inspector;

var config = new Configuration { Host = "0.0.0.0", Port = 5000 };
var server = new MyServer(config);
await server.StartAsync();

// pass the SAME config (it owns the metrics) and the server:
var inspector = new InspectorServer(config, server, port: 9090, host: "localhost");
inspector.Start();

// browse http://localhost:9090/      (auto-refreshing HTML)
// scrape http://localhost:9090/metrics   (JSON)

// ... on shutdown:
inspector.Dispose();   // stops the HTTP listener
```

`/metrics` returns a flat JSON object:

```json
{
  "activeConnections": 128,
  "messagesSent": 90210,
  "messagesReceived": 88541,
  "connectionsAccepted": 512,
  "connectionsRejected": 3,
  "reliableRetransmits": 17,
  "reliableAcksReceived": 40233,
  "handshakesDropped": 1,
  "inboundDropped": 0
}
```

## API

| Member | Purpose |
|---|---|
| `new InspectorServer(config, server, port = 9090, host = "localhost")` | Build the inspector (`config` supplies `Metrics`, `server` supplies `ActiveConnections`) |
| `Start()` | Begin serving `/` and `/metrics` |
| `Dispose()` | Stop the listener |

**Fields** (from `config.Metrics` + `server.ActiveConnections`): `activeConnections`, `messagesSent`, `messagesReceived`, `connectionsAccepted`, `connectionsRejected`, `reliableRetransmits`, `reliableAcksReceived`, `handshakesDropped`, `inboundDropped`.

## Notes

- **Not for the public internet.** The dashboard exposes internal counters and has **no authentication**. Bind to `localhost` (the default), or if you pass `host: "0.0.0.0"` (which binds `http://+:{port}/`), keep it behind a firewall, VPN, or an authenticating reverse proxy.
- **Binding on `0.0.0.0`/`+`** may need a URL ACL (`netsh http add urlacl`) or elevated privileges on Windows; on Linux/macOS it binds directly.
- **Read-only.** The inspector never mutates the server — it only reads `config.Metrics` and `server.ActiveConnections`. Pass the *same* `Configuration` instance you gave the server so it reports the live metrics.
- For orchestrator liveness/readiness probes rather than a human dashboard, use [`SetNet.HealthChecks`](https://www.nuget.org/packages/SetNet.HealthChecks).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
