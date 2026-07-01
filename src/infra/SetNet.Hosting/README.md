# SetNet.Hosting

**Generic Host / ASP.NET Core integration for [SetNet](https://www.nuget.org/packages/SetNet).**

Run a SetNet server as an `IHostedService`, so it starts with the host and shuts down gracefully alongside the rest of your app.

```csharp
using SetNet.Hosting;

builder.Services.AddSetNetServer(sp => new MyServer(new Configuration { Host = "0.0.0.0", Port = 5000 }));
```

The server's accept loop starts on host startup and `StopAsync` is called on shutdown. Pairs well with [`SetNet.DependencyInjection`](https://www.nuget.org/packages/SetNet.DependencyInjection) and [`SetNet.HealthChecks`](https://www.nuget.org/packages/SetNet.HealthChecks).

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
