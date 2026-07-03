# SetNet.Ticks

**One central update loop for a SetNet server.** Instead of every system running its own `System.Threading.Timer`, or you calling `Tick()`/`Update()` by hand all over the place, register everything into named **channels** — each with its own **rate (Hz)** and **priority** — and drive them all from one place.

- **Per-channel fixed timestep** — deterministic; `30 Hz` movement, `10 Hz` AI, `1 Hz` housekeeping, all independent.
- **Two ways to drive it** — an internal timer (`Start()`, typical dedicated server) or `Pump(dt)` from your own loop (Unity `Update`/`FixedUpdate`, a custom game loop).
- **Priority ordering** — higher-priority channels tick first within a pump (e.g. movement before AI).
- **Anti-spiral** — a per-channel cap on catch-up substeps, so a stall can't trigger an unbounded burst.
- **Automatic subscription** — set one ambient host (`new TickScheduler().MakeCurrent()`) and the library's game-loop systems (`MobServer`, `LocomotionSystem`, `SpawningServer`) subscribe themselves when created — you don't register each one. Behaviour trees / state machines plug in via `Bind(context)`.

**Zero dependencies.** This is a *foundation*: the game-loop modules (`SetNet.Mobs`, `SetNet.Locomotion`, `SetNet.Spawning`) depend on **this**, not the other way round — so `SetNet` core stays a pure transport/data layer with no notion of ticks. No wire protocol; a pure server-side scheduler.

## The interface (this package)

```csharp
public readonly struct TickInfo
{
    public double DeltaMs { get; }      // this channel's fixed step, in ms  (library modules)
    public float  DeltaSeconds { get; } // …and in seconds                   (Unity-friendly)
    public long   Frame { get; }        // scheduler frame counter
}

public interface ITickable      { void Tick(in TickInfo tick); }
public interface IAsyncTickable { Task TickAsync(TickInfo tick); }
```

`ITickable`, `IAsyncTickable`, the registration seam `ITickHost`, the ambient `TickHost.Current`, and `TickScheduler` (which implements `ITickHost`) all live in **this** package (namespace `SetNet.Ticks`). Modules that want to be tickable reference `SetNet.Ticks` — a tiny, zero-dependency foundation — rather than pulling the concept into `SetNet` core.

## Quick start — automatic subscription (recommended)

Set one ambient host with `MakeCurrent()` **before** your `UseXxx(...)` calls. Each game-loop system then subscribes itself — into its own channel, at its own rate — and runs off the scheduler instead of its own timer. You register nothing by hand:

```csharp
using SetNet.Ticks;

var ticks = new TickScheduler().MakeCurrent();   // ← everything created after this auto-subscribes

var loco     = server.UseLocomotion(geo);        // → channel "locomotion" @ Hz, priority 100
var mobs     = server.UseMobs();                 // → channel "mobs"       @ TickRateHz, priority 50
var spawning = server.UseSpawning(mobs, opts);   // → channel "spawning"   @ 1000/TickIntervalMs, priority 10

ticks.Start();                                   // one place drives all of them
// …or, from your own loop each frame:  ticks.Pump(realDeltaMs);
```

Every mob rides `MobServer`, every mover rides `LocomotionSystem`, so subscribing the *systems* is enough — you never touch individual mobs/movers. Behaviour trees used inside mob brains ride `MobServer` too. Standalone trees/machines plug in explicitly (they need a context):

```csharp
ticks.Channel("ai", 10).Add(tree.Bind(context));   // a tree not owned by a mob
ticks.Channel("slow", 1).Add(t => RegenTick(t.DeltaSeconds));
```

Default channels/rates/priorities are overridable per system (`MobOptions.TickChannel`/`TickPriority`, `LocomotionOptions.TickChannel`/…). Opt one system out with `AutoTick = false` (it falls back to its own timer).

### Manual registration (no ambient host)

If you'd rather wire it explicitly, skip `MakeCurrent()` and add systems to channels yourself (turn their own timers off so they don't double-tick):

```csharp
var mobs = server.UseMobs(new MobOptions { AutoTick = false, UseInternalTimer = false });
var loco = server.UseLocomotion(geo, new LocomotionOptions { AutoTick = false, UseInternalTimer = false });

var ticks = new TickScheduler();
ticks.Channel("movement", 30, priority: 10).Add(loco);   // LocomotionSystem : ITickable
ticks.Channel("ai",       10, priority: 5).Add(mobs);    // MobServer : IAsyncTickable
ticks.Start(baseHz: 60);                                 // base timer ≥ your fastest channel
```

### The behaviour-tree pain point

A `BehaviorTree<T>` doesn't tick all your trees for you — you'd call `tree.Tick(ctx, dt)` per entity. `Bind(ctx)` turns a tree into an `ITickable`, so **every tree updates from the one scheduler**:

```csharp
foreach (var mob in mobs)
    aiChannel.Add(mob.Tree.Bind(mob.Context));   // registered once; ticked together at 10 Hz
```

## API

### `TickScheduler`

| Member | Meaning |
|---|---|
| `TickScheduler MakeCurrent()` | Make this the ambient `TickHost.Current` so systems auto-subscribe. Returns this; call before your `UseXxx(...)`. Cleared on `Dispose`. |
| `IDisposable Register(ITickable / IAsyncTickable, channel, hz, priority)` | The `ITickHost` seam systems use to auto-subscribe; you can call it directly too. |
| `TickChannel Channel(string name, int hz = 10, int priority = 0)` | Get or create a channel. Hz/priority apply only on first creation. |
| `void Start(int baseHz = 60)` | Start the internal timer driver. Set `baseHz` ≥ your fastest channel. Restarts if already running. |
| `void Stop()` | Stop the internal timer (registrations kept). |
| `void Pump(double realDtMs)` | Advance every channel by real elapsed time. Re-entrant calls are ignored. Call this yourself when driving externally. |
| `bool Paused { get; set; }` | When true, pumps advance the frame counter but tick nothing. |
| `long Frame { get; }` | Current frame number. |
| `IReadOnlyList<TickChannel> Channels { get; }` | Channels, highest priority first. |
| `void Dispose()` | Stop the driver and clear all channels. |

### `TickChannel`

| Member | Meaning |
|---|---|
| `string Name` | Channel identity. |
| `int Hz` | Ticks per second; items get a fixed `1000/Hz` ms step. Changeable at runtime. |
| `int Priority` | Higher ticks earlier within a pump. Changeable at runtime. |
| `bool Enabled` | Toggle to pause just this channel. |
| `int MaxSubstepsPerPump` | Cap on catch-up steps per pump (default 5); backlog beyond it is dropped. |
| `int Count` | Registered item count. |
| `IDisposable Add(ITickable)` / `Add(IAsyncTickable)` | Register a (sync/async) tickable. Async is fire-and-forget per step — guard overruns (Mobs already does). |
| `IDisposable Add(Action<TickInfo>)` / `Add(Action)` / `Add(Func<TickInfo, Task>)` | Register a callback. |

Dispose the handle returned by any `Add` to unregister.

## Notes

- **Async tickables** (`IAsyncTickable`, `Func<TickInfo,Task>`) are invoked fire-and-forget per step, *not* awaited. If your work can overrun a step, guard against overlapping runs — `MobServer` already self-guards, so a slow AI tick simply skips rather than piling up.
- **Fixed timestep**: each item is called with `DeltaMs = 1000/Hz`, regardless of the base drive rate. Real time is absorbed by a per-channel accumulator.
- **Housekeeping timers** in other modules (Auth session sweep, Auction settle, Matchmaking, StatusEffects expiry) keep their own low-frequency timers — they're independent of the game loop. Route them through a `slow` channel only if you want a single clock; they don't need it.

## License

MIT
