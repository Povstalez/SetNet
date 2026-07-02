<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Mobs.StateSync

**StateSync replication adapter for [`SetNet.Mobs`](https://www.nuget.org/packages/SetNet.Mobs).**

`SetNet.Mobs` keeps replication behind a seam (`IMobReplication`) so it has *no* dependency on any replication package. This adapter plugs that seam into [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync): each mob becomes a `NetworkEntity` of a registered archetype (interpolated position, velocity, health, target hash, cast id + remaining), so nearby players observe mobs through the same delta-compressed snapshot stream as every other entity — no separate mob replication wire.

## Install

```bash
dotnet add package SetNet.StateSync
dotnet add package SetNet.Mobs
dotnet add package SetNet.Mobs.StateSync
```

## Usage

**Server** — wire it in one line when building `MobOptions`:

```csharp
using SetNet.Mobs;
using SetNet.Mobs.StateSync;

StateSyncRuntime.Enable();
MobsRuntime.Enable();

var world = server.UseStateSync();   // your StateSync replication world

server.UseMobs(new MobOptions
{
    Replication    = world.StateSyncReplication(),   // mobs replicate over StateSync
    PlayerPosition = key => positions.Get(key),
    AllPlayers     = () => positions.Keys,
});
```

**Client** — register the identical mob archetype schema so field indices line up:

```csharp
ReplicaRegistry.Register(StateSyncMobReplication.BuildSchema());   // same archetype id (default 4100)
```

Nearby players must be observers of the StateSync world (they normally already are). Pass a custom archetype id to both `world.StateSyncReplication(id)` and `StateSyncMobReplication.BuildSchema(id)` if 4100 collides with one of your own archetypes.

## What replicates

| Field | Type | Notes |
|---|---|---|
| Position | `Vector3` | interpolated, quantized to 1 cm |
| Velocity | `Vector3` | for dead-reckoning |
| Health / MaxHealth | `Float` | health interpolated |
| Target hash | `Int` | stable hash of the current target key (0 = none) |
| Cast ability hash | `Int` | stable hash of the casting ability id (0 = none) |
| Cast remaining | `Int` | milliseconds left on the telegraphed cast |

Discrete cues the raw snapshot doesn't convey (spawn/attack/aggro/death VFX) still ride the `Channels.Mobs` events on the `MobClient` — this adapter only handles the continuous state.

## License

MIT
