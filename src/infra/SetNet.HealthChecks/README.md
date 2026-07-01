<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.HealthChecks

**ASP.NET Core health check for a [SetNet](https://www.nuget.org/packages/SetNet) server.**

Exposes a SetNet server's liveness and its active connection count through the standard `IHealthCheck` pipeline, so Kubernetes liveness/readiness probes, load balancers, and monitoring can watch the realtime server the same way they watch the rest of your app. Optionally reports **Degraded** once the connection count crosses a warning threshold, which a load balancer can use to shed new traffic before the node is saturated.

Designed to sit alongside [`SetNet.Hosting`](https://www.nuget.org/packages/SetNet.Hosting) (which registers the server as a singleton you can resolve here).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.HealthChecks
```

## Usage

```csharp
using SetNet.HealthChecks;

builder.Services.AddHealthChecks()
    .AddSetNet(server, name: "setnet", degradedAtConnections: 4500);

// later, map the endpoint:
app.MapHealthChecks("/healthz");
```

When the server is registered in DI (e.g. via [`SetNet.Hosting`](https://www.nuget.org/packages/SetNet.Hosting)), resolve the same instance:

```csharp
builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration(
        "setnet",
        sp => new SetNetHealthCheck(sp.GetRequiredService<BaseServer>(), degradedAtConnections: 4500),
        failureStatus: default,
        tags: default));
```

The check always attaches an `activeConnections` datum, so `/healthz` responses (with the detailed writer) carry the live count:

```json
{
  "status": "Healthy",
  "entries": {
    "setnet": {
      "status": "Healthy",
      "description": "SetNet server healthy (128 active connections).",
      "data": { "activeConnections": 128 }
    }
  }
}
```

## API

| Member | Purpose |
|---|---|
| `IHealthChecksBuilder.AddSetNet(server, name = "setnet", degradedAtConnections = 0)` | Registers the check |
| `SetNetHealthCheck(server, degradedAtConnections = 0)` | The `IHealthCheck` itself, if you register it manually |

**Result:**

| Condition | Status | Data |
|---|---|---|
| Always (unless degraded) | `Healthy` | `activeConnections` = current count |
| `degradedAtConnections > 0` **and** `ActiveConnections >= degradedAtConnections` | `Degraded` | `activeConnections` = current count |

## Notes

- **Threshold is opt-in.** `degradedAtConnections = 0` (the default) means the check never reports Degraded — it only ever returns Healthy with the connection count.
- **Liveness, not deep health.** The check reports Healthy as long as the server object is alive and reachable; it reflects `BaseServer.ActiveConnections`, not per-transport internals. Combine with [`SetNet.Inspector`](https://www.nuget.org/packages/SetNet.Inspector) or `NetworkMetrics` for richer diagnostics.
- The count comes from `BaseServer.ActiveConnections`, so it tracks whatever transports (TCP / UDP / Both) the server is running.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
