<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync

**Server-authoritative entity replication for [SetNet](https://www.nuget.org/packages/SetNet).**

Stream a world of entities from an authoritative server to clients with **fixed-rate, delta-compressed snapshots** over the unreliable channel, **reliable spawns/despawns**, **client-side interpolation**, **interest management**, and an **input channel** for client prediction. The core is **engine-agnostic** — it runs on a headless dedicated server and any .NET client. For Unity, add **SetNet StateSync for Unity** (NetworkObject/NetworkTransform/NetworkAnimator/NetworkRigidbody).

Composition, no base class. Depends only on `SetNet`.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
```

At startup (both ends): register a serializer, call `StateSyncRuntime.Enable()`, and register your archetype schemas **identically**.

```csharp
SetNetSerializer.Use(new MessagePackNetSerializer());
StateSyncRuntime.Enable();

const ushort Player = 1;
ReplicaRegistry.Register(ReplicaSchema.Create(Player)
    .Field(FieldType.Vector3, interpolate: true, precision: 0.001f)  // 0: position (quantized to mm)
    .Field(FieldType.Quaternion, interpolate: true)                  // 1: rotation
    .Field(FieldType.Float, interpolate: true)                       // 2: health
    .Build());
```

## Server

```csharp
var world = server.UseStateSync(new StateSyncOptions { TickRate = 30 });

// spawn + mutate fields whenever; the tick samples them
var e = world.Spawn(Player, owner: peerId);
e.SetVec3(0, new Vec3(x, y, z));
e.SetQuat(1, rotation);
e.SetFloat(2, 100);

world.InputReceived += (peer, seq, payload) => { /* apply owned-entity input */ };
```

By default every connecting peer becomes an observer. Set `StateSyncOptions.AutoObserve = false` and call `world.AddObserver(peer)` yourself once a player is truly ready (e.g. after auth or joining a room), so you don't replicate the game to peers still in the lobby.

## Client

```csharp
var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 100 });

repl.EntitySpawned   += view => { /* create your object for view.NetId / view.ArchetypeId */ };
repl.EntityDespawned += view => { /* destroy it */ };

// each frame:
repl.Update();                                   // advance interpolation
foreach (var v in repl.Entities)
{
    var pos = v.GetVec3(0);                       // interpolated
    var rot = v.GetQuat(1);
}

// owned-entity input for prediction:
var seq = repl.SendInput(inputBytes);
var acked = repl.LastProcessedInput;             // reconcile against this
```

## How it works

- **Delta compression:** each snapshot carries only the fields that changed since the client's acknowledged baseline (Quake-3-style tick/baseline history). A new or re-entering entity is sent full, then deltas. Lost snapshots self-heal on the next tick.
- **Reliable lifecycle, unreliable state:** spawns/despawns ride the reliable channel (`Both`-mode TCP); snapshots ride unreliable UDP. On a TCP-only transport everything is reliable/ordered and it still works.
- **Interpolation:** the client renders `InterpolationDelayMs` behind the newest snapshot, lerping floats/vectors and nlerp-ing quaternions between buffered samples. Set the delay to 0 to snap to the latest.
- **Interest management:** `StateSyncOptions.Interest` decides what each observer sees. `AllInterest` (default) or `DistanceInterest` (area-of-interest), or your own `IInterestManager`. Entities entering/leaving an observer's set are spawned/despawned for them.
- **Quantization:** give a float/vector field a `precision` to send it as a scaled integer (smaller packets).

## Options

| Option | Default | Meaning |
|---|---|---|
| `TickRate` | `30` | Snapshots per second |
| `InterpolationDelayMs` | `100` | Render delay for smoothing (0 = snap) |
| `MaxSnapshotHistory` | `32` | Delta baseline history depth |
| `Interest` | `AllInterest` | Per-observer visibility |
| `AutoObserve` | `true` | Auto-observe peers on connect |

## Notes

- Node-local (entities are live objects on one server node). A multi-node setup shards worlds or hands players between nodes.
- UDP snapshots are unauthenticated at the packet level — put auth over the reliable channel (`SetNet.Auth`), and use TLS/`Both` for sensitive data.
- Reserved wire types `65518–65522`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
