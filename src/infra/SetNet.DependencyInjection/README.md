<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.DependencyInjection

**Constructor injection for your [SetNet](https://www.nuget.org/packages/SetNet) message handlers.**

By default SetNet discovers your `[MessageHandler]` classes by reflection and instantiates them with a parameterless constructor. This package bridges handler construction to a `Microsoft.Extensions.DependencyInjection` container, so your handlers can take **injected dependencies** — services, loggers, repositories, game state — through their constructors instead of a bare `new`.

Use it whenever your handlers need collaborators, or when you already run on the .NET Generic Host / ASP.NET Core. Pairs naturally with [`SetNet.Hosting`](https://www.nuget.org/packages/SetNet.Hosting) and [`SetNet.HealthChecks`](https://www.nuget.org/packages/SetNet.HealthChecks).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.DependencyInjection
```

## Usage

Call `provider.UseSetNetHandlers()` **before** you construct your `BaseClient` / `BaseServer` — handlers are discovered and built when the executor is created inside that constructor, so the seam must already be in place.

```csharp
using Microsoft.Extensions.DependencyInjection;
using SetNet.DependencyInjection;

var services = new ServiceCollection();

// your app dependencies:
services.AddSingleton<IPlayerStore, SqlPlayerStore>();
services.AddSingleton<ILeaderboard, Leaderboard>();

// (optional) register the handler itself so it's resolved from the container
// instead of activated ad hoc — do this if the handler is a singleton or
// needs container-managed lifetime/disposal:
services.AddSingleton<PlayerMoveHandler>();

var provider = services.BuildServiceProvider();

provider.UseSetNetHandlers();          // <-- install the seam FIRST

var server = new MyServer(config);     // handlers discovered & built here
await server.StartAsync();
```

A handler with an injected dependency looks exactly like a normal handler — the constructor argument is filled by the container:

```csharp
[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler<PlayerMoveMessage>
{
    private readonly IPlayerStore _players;

    public PlayerMoveHandler(IPlayerStore players) => _players = players;   // injected

    public async Task HandleAsync(BasePeer peer, PlayerMoveMessage message)
    {
        await _players.UpdatePositionAsync(message.PlayerId, message.X, message.Y);
    }
}
```

## How it works

`UseSetNetHandlers()` sets the core `HandlerActivator.Factory` seam to route every handler through your provider:

| Case | Resolution |
|---|---|
| Handler type **is registered** in the container | Resolved via `provider.GetService(type)` (honours its registered lifetime) |
| Handler type is **not registered** | Built with `ActivatorUtilities.CreateInstance` — constructor injection still works, pulling each ctor arg from the container |

So you get injection either way; registering the handler only matters when you want the container to own its lifetime.

## Notes

- **Ordering is load-bearing.** `UseSetNetHandlers()` must run before the `BaseClient` / `BaseServer` constructor. Called afterward, the executor has already built its handlers with the default parameterless activator.
- **One process-wide factory.** The seam is a single static `HandlerActivator.Factory`; the last provider to call `UseSetNetHandlers()` wins. Typical apps have one provider, so this is a non-issue.
- **Lifetime.** Handlers are constructed once at executor build time. Prefer singleton/transient collaborators; for per-message scoping, inject an `IServiceScopeFactory` and open a scope inside `HandleAsync`.
- Works identically on client (`BaseClient`) and server (`BaseServer`).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
