# SetNet.HealthChecks

**Health check for [SetNet](https://www.nuget.org/packages/SetNet).**

Expose a SetNet server's liveness and active connection count through the standard `IHealthCheck` pipeline, so Kubernetes, load balancers and monitoring can probe it.

```csharp
using SetNet.HealthChecks;

builder.Services.AddHealthChecks()
    .AddSetNet(server, name: "setnet", degradedAtConnections: 4500);
```

Reports `Healthy` with an `activeConnections` datum, or `Degraded` once the connection count crosses the threshold.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
