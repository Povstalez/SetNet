<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.DependencyInjection

**Constructor injection for every reflection-discovered [SetNet](https://www.nuget.org/packages/SetNet) component.**

By default SetNet discovers your classes by reflection and instantiates them with a parameterless constructor. This package bridges that construction to a `Microsoft.Extensions.DependencyInjection` container, so they can take **injected dependencies** — services, loggers, repositories, game state — through their constructors instead of a bare `new`. It covers **all** discovered component kinds through one seam:

- `[MessageHandler]` message handlers (server & client),
- `[ProtocolChannel]` channel services and `[Op]` router classes (the unified protocol),
- client `[Event]` push handlers,
- `[RpcMethod]` RPC handlers (`SetNet.Rpc`).

Use it whenever your handlers need collaborators, or when you already run on the .NET Generic Host / ASP.NET Core. Pairs naturally with [`SetNet.Hosting`](https://www.nuget.org/packages/SetNet.Hosting) and [`SetNet.HealthChecks`](https://www.nuget.org/packages/SetNet.HealthChecks).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.DependencyInjection
```

## Usage

Call `provider.UseSetNet()` **before** you construct your `BaseClient` / `BaseServer` (and before the first RPC / protocol use) — components are discovered and built lazily on first use, so the seam must already be in place. (`UseSetNetHandlers()` is a backwards-compatible alias — it now covers channel services, events and RPC too, not just handlers.)

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

provider.UseSetNet();                  // <-- install the seam FIRST (handlers + channels + events + RPC)

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

The same works for a channel service or an RPC handler — the constructor argument is filled by the container:

```csharp
[ProtocolChannel(Channels.Rooms)]
public class RoomsChannelService : IChannelService
{
    private readonly IRoomAudit _audit;
    public RoomsChannelService(IRoomAudit audit) => _audit = audit;   // injected
    public Task HandleAsync(ChannelRequest request) { /* … */ }
}

[RpcMethod((ushort)Rpc.GetProfile)]
public class GetProfileHandler : IRpcHandler<ProfileReq, ProfileResp>
{
    private readonly IPlayerStore _players;
    public GetProfileHandler(IPlayerStore players) => _players = players;   // injected
    public Task<ProfileResp> HandleAsync(BasePeer peer, ProfileReq req) { /* … */ }
}
```

## How it works

`UseSetNet()` sets the one core `HandlerActivator.Factory` seam that **every** discovered component goes through (handlers, channel services, `[Op]` classes, `[Event]` handlers, RPC handlers):

| Case | Resolution |
|---|---|
| Type **is registered** in the container | Resolved via `provider.GetService(type)` (honours its registered lifetime) |
| Type is **not registered** | Built with `ActivatorUtilities.CreateInstance` — constructor injection still works, pulling each ctor arg from the container |

So you get injection either way; registering the component only matters when you want the container to own its lifetime.

> **Reaching your `UseXxx()` systems from a handler.** To inject `InventoryServer`, `LocomotionSystem`, etc. into a handler, register them in the container after you create the server (`services.AddSingleton(server.UseInventory())`) — or, for the game/Unity style where that's awkward, use [`SetNet.Services`](https://www.nuget.org/packages/SetNet.Services) as a locator.

## Notes

- **Ordering is load-bearing.** `UseSetNet()` must run before the `BaseClient` / `BaseServer` constructor and before the first RPC/protocol use. Called afterward, that component set has already been built with the default parameterless activator (some discovery is cached on first use).
- **One process-wide factory.** The seam is a single static `HandlerActivator.Factory`; the last provider to call `UseSetNet()` wins. Typical apps have one provider, so this is a non-issue.
- **Lifetime.** Components are constructed once at discovery time. Prefer singleton/transient collaborators; for per-message scoping, inject an `IServiceScopeFactory` and open a scope inside `HandleAsync`.
- Works identically on client (`BaseClient`) and server (`BaseServer`).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
