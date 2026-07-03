# SetNet.Services

**A tiny service locator so you stop hand-storing every `UseXxx()` instance.** Each `server.UseInventory()`, `server.UseDialogue()`, `server.UseChat()` returns a hub instance that you normally have to keep in a field and thread into every class that needs it. Register each once here and resolve it anywhere by type.

```csharp
using SetNet.Services;

var hub = new ServiceHub().MakeCurrent();          // ambient — set once at startup

hub.Add(server.UseLocomotion(geo));
hub.Add(server.UseMobs(opts));
hub.Add(server.UseDialogue());
hub.Add(server.UseChat());

// …later, in ANY class (a handler, a mob brain, a helper) — no constructor plumbing:
var dialogue = Service.Get<DialogueServer>();
var loco     = Service.Get<LocomotionSystem>();
```

Depends only on `SetNet`. Not a full DI container — it locates the singletons your modules already create; it constructs nothing.

## Three ways to use it (mix freely)

### Ambient (simplest)
One process-wide hub. `new ServiceHub().MakeCurrent()`, then `Service.Get<T>()` from anywhere.

```csharp
new ServiceHub().MakeCurrent();
Service.Get<WalletServer>().DepositAsync(key, 100);
```

### Per-server / per-client (isolated)
A hub bound to a specific `BaseServer`/`BaseClient` (handy when you run more than one in a process — tests, co-hosting). Created on first use, lives as long as the owner.

```csharp
server.Services().Add(server.UseInventory());
// elsewhere with the same server:
var inv = server.Services().Get<InventoryServer>();
```

### Explicit
Hold the `ServiceHub` yourself and pass it where you like — no ambient state at all.

## Registering

`Add<T>` returns the instance, so you register and capture in one line:

```csharp
var mobs = hub.Add(server.UseMobs(opts));   // registered AND captured
```

`AddAll(params object[])` registers several at once (by runtime type):

```csharp
hub.AddAll(server.UseInventory(), server.UseWallet(), server.UseTrade(inv));
```

A second `Add` of the same type replaces the first.

## Resolving

| Call | Behaviour |
|---|---|
| `hub.Get<T>()` / `Service.Get<T>()` | Returns the instance; **throws** if none registered (message hints at the missing `UseXxx`). |
| `hub.TryGet<T>(out v)` / `Service.TryGet<T>(out v)` | `false` if none (no throw). |
| `hub.GetOrNull<T>()` / `Service.GetOrNull<T>()` | `null` if none. |
| `hub.Has<T>()` | Membership check. |
| `hub.Remove<T>()` | Unregister. |
| `hub.All` | Snapshot of everything registered. |

`Service.*` is a static shortcut over `ServiceHub.Current`. It throws (Get) or returns null/false (GetOrNull/TryGet) when no ambient hub is set.

## Example

Instances are keyed by their **static type**, which is the `UseXxx` return type — so resolve by that concrete type (`InventoryServer`, `LocomotionSystem`, `DialogueServer`, …). See **`examples/MobBrains`**, where three mob brains each reach the (moving) player through the hub with zero constructor wiring.

## Note vs. full DI

If you already use `Microsoft.Extensions.DependencyInjection`, keep using it — register the `UseXxx()` results as singletons there instead. `SetNet.Services` is the zero-dependency middle ground for projects (Unity, small servers) that just want "reach my systems from anywhere" without a container. It does **not** auto-register — you `Add(...)` each once; that's the one line that replaces a scattered field.

## License

MIT
