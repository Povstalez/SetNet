<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Hosting

**Run a [SetNet](https://www.nuget.org/packages/SetNet) server on the .NET Generic Host / ASP.NET Core.**

Registers your SetNet server as a singleton and runs it as an `IHostedService`: the accept loop starts when the host starts, and the server is stopped gracefully on shutdown. This lets your realtime/game server participate in the standard host lifecycle — DI, configuration, logging, graceful termination — instead of being bootstrapped by hand in `Main`.

Pairs naturally with [`SetNet.DependencyInjection`](https://www.nuget.org/packages/SetNet.DependencyInjection) (inject dependencies into handlers) and [`SetNet.HealthChecks`](https://www.nuget.org/packages/SetNet.HealthChecks) (k8s / load-balancer probes).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Hosting
```

## Usage

```csharp
using SetNet.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// register the server as a singleton + run it as a hosted service:
builder.Services.AddSetNetServer(sp =>
{
    var config = new Configuration { Host = "0.0.0.0", Port = 5000 };
    return new MyServer(config);
});

var host = builder.Build();
await host.RunAsync();   // MyServer.StartAsync() runs on startup, StopAsync() on shutdown
```

The factory receives the `IServiceProvider`, so you can pull configuration or dependencies from the container when building the server:

```csharp
builder.Services.AddSingleton<IPlayerStore, SqlPlayerStore>();

builder.Services.AddSetNetServer(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>()
                   .GetSection("SetNet").Get<ServerOptions>();
    return new MyServer(new Configuration { Host = "0.0.0.0", Port = config.Port });
});
```

Because the server is registered as a singleton `BaseServer`, other services (a health check, an inspector, a background job) can resolve the same instance:

```csharp
builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration("setnet",
        sp => new SetNetHealthCheck(sp.GetRequiredService<BaseServer>()),
        default, default));
```

## API

| Member | Purpose |
|---|---|
| `IServiceCollection.AddSetNetServer(Func<IServiceProvider, BaseServer>)` | Registers the server as a singleton `BaseServer` and adds `SetNetServerHostedService` |
| `SetNetServerHostedService` | `IHostedService` that calls `StartAsync()` on host start and `StopAsync()` on shutdown |

## Notes

- **Non-blocking startup.** `StartAsync` kicks off the server's accept loop without awaiting it, so host startup isn't blocked by the long-running loop. The loop runs until `StopAsync` (host shutdown).
- **One server per registration.** Call `AddSetNetServer` once; the server is a singleton `BaseServer`. To host more than one server, wrap them yourself or register additional `IHostedService`s.
- **If you also use [`SetNet.DependencyInjection`](https://www.nuget.org/packages/SetNet.DependencyInjection)**, call `provider.UseSetNetHandlers()` before the server is constructed — the simplest place is at the top of your `AddSetNetServer` factory, so it runs before `new MyServer(...)` builds the executor.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
