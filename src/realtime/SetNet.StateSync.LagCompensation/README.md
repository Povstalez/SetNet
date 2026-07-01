<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.LagCompensation

**Server-side lag compensation for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

In a server-authoritative game each client sees the world **delayed** — by its interpolation buffer plus network latency. So when a player aims at a target and fires, the target on their screen is where it was `interpolationDelay + RTT/2` ago, not where the server has it *now*. Judging the hit against the server's current positions feels unfair ("I clearly hit them!").

`LagCompensator` fixes this the way competitive shooters do: it records a short **history** of every entity's position each server tick, and lets you **rewind** the world to the moment the shooter actually saw. Test the shot against those historical positions and the hit registers where the player expected it — while the server stays authoritative.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.StateSync.LagCompensation
```

## Usage

Create one compensator per world, feed it positions every tick, and rewind on demand. You supply how to read an entity's position — the core doesn't know which field is "position".

```csharp
using SetNet.StateSync;
using SetNet.StateSync.LagCompensation;

var world = server.UseStateSync(new StateSyncOptions { TickRate = 30 });

// field 0 is the Vector3 position; keep 1s of history
var lag = new LagCompensator(e => e.GetVec3(0), historyMs: 1000);

// call once per server tick, from your simulation loop:
lag.Capture(world.Entities);

// when a client fires, rewind to what THEY saw:
double rewind = interpolationDelaySeconds + rttSeconds / 2.0;   // ~ what the shooter rendered
Vec3? past = lag.PositionAgo(targetNetId, secondsAgo: rewind);
if (past.HasValue && RayHits(shot, past.Value))
    ApplyDamage(targetNetId, damage);
```

`PositionAgo` interpolates between the two recorded frames bracketing that instant, so you get a smooth past position even between ticks. If you prefer to rewind to an absolute timestamp on the compensator's own clock, use `PositionAt` with a time derived from `NowSeconds`:

```csharp
double shotTime = lag.NowSeconds - rewind;
Vec3? p = lag.PositionAt(targetNetId, shotTime);
```

## API

| Member | Purpose |
|---|---|
| `new LagCompensator(Func<NetworkEntity, Vec3> positionSelector, int historyMs = 1000)` | Create a compensator keeping `historyMs` of history (floored at 50 ms) |
| `void Capture(IEnumerable<NetworkEntity> entities)` | Record all entity positions at the current time — call once per tick |
| `Vec3? PositionAgo(uint netId, double secondsAgo)` | Interpolated position `secondsAgo` in the past, or `null` if unknown/too old |
| `Vec3? PositionAt(uint netId, double time)` | Interpolated position at an absolute compensator time, or `null` |
| `double NowSeconds` | The compensator's monotonic clock (seconds) — use it to compute rewind offsets consistently |

## Notes

- **Position selector is required** because the core doesn't know which field holds a position — read the same field you declared as the position in your `ReplicaSchema` (e.g. `e.GetVec3(0)`).
- **Rewind amount is game-specific.** `interpolationDelay + RTT/2` is the standard estimate of what the shooter rendered; measure RTT per peer for accuracy. Rewinding too far punishes honest players; too little defeats the point.
- **History window:** frames older than `historyMs` are dropped, so a rewind further back than your history returns `null`. Size `historyMs` to cover your worst-case `interpolationDelay + RTT/2`.
- **Server-only.** Lag compensation is an authority-side decision; clients never rewind. It uses a monotonic `Stopwatch`, independent of wall-clock changes.
- Queries and captures are thread-safe (guarded internally), so you can `Capture` on your tick thread and rewind from a handler.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
