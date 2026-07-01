# SetNet.DependencyInjection

**Microsoft DI integration for [SetNet](https://www.nuget.org/packages/SetNet).**

Construct your `[MessageHandler]` classes through a `IServiceProvider`, so handlers get **constructor-injected dependencies** (services, repositories, loggers) instead of a parameterless `new`.

```csharp
using SetNet.DependencyInjection;

var provider = services.BuildServiceProvider();
provider.UseSetNetHandlers();     // BEFORE constructing your BaseClient/BaseServer

// now handlers can inject dependencies:
[MessageHandler(1)]
public class BuyHandler : IServerMessageHandler<Buy>
{
    public BuyHandler(IShopService shop) { ... }
    public Task HandleAsync(BasePeer peer, Buy msg) { ... }
}
```

Registered handler types are resolved from the container; unregistered ones are still built with constructor injection via `ActivatorUtilities`.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
