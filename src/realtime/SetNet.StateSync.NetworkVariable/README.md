<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.NetworkVariable

**Typed, change-tracked variables for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

On the client, a replicated entity is a `NetworkEntityView` whose fields you read by index and type — `view.GetFloat(2)`, `view.GetVec3(0)`. That's fast but bare: you remember which index is which, you re-read every field every frame, and you write your own "did it change?" checks to react to updates.

`NetworkVariable<T>` is a small read-side convenience over that view. Bind one to a field and it gives you a typed `Value`, a `Changed` event, and a `Poll()` you call once per frame — it re-reads the interpolated value and fires `Changed` only when it actually changes. Great for driving UI (health bars, ammo counters, name labels) off replicated fields without polling boilerplate.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.StateSync.NetworkVariable
```

## Usage

Bind variables to a client-side `NetworkEntityView` — e.g. when the entity spawns — then `Poll()` them each frame after `ClientReplication.Update()`.

```csharp
using SetNet.StateSync;
using SetNet.StateSync.NetworkVariable;

var replication = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 100 });

replication.EntitySpawned += view =>
{
    // field 2 is a Float health value; field 0 is a Vector3 position
    var health = view.Watch<float>(2);   // NetworkVariable<float>
    var pos    = view.Watch<Vec3>(0);    // NetworkVariable<Vec3>

    health.Changed += hp  => UpdateHealthBar(view.NetId, hp);
    pos.Changed    += p   => MoveNameplate(view.NetId, p);

    Register(view.NetId, health, pos);   // keep them to Poll() each frame
};

// once per frame, AFTER advancing interpolation:
replication.Update();
foreach (var v in AllWatched())
    v.Poll();                            // raises Changed when the interpolated value moved

float currentHp = healthVar.Value;       // read anytime, no polling needed
```

`Watch<T>(index)` is an extension on `NetworkEntityView`; it returns a `NetworkVariable<T>` bound to that field. Reading `Value` always reflects the view's current (interpolated) value; `Changed` fires only from `Poll()`.

## API

| Member | Purpose |
|---|---|
| `NetworkEntityView.Watch<T>(int index)` | Bind a typed variable to field `index` (extension) → `NetworkVariable<T>` |
| `T NetworkVariable<T>.Value` | The current (interpolated) value — read anytime |
| `event Action<T> Changed` | Raised during `Poll()` when the value differs from the last poll |
| `void Poll()` | Re-read the value and raise `Changed` if it changed — call once per frame |

**Supported `T`:** `float`, `double`, `int`, `long`, `bool`, `string`, `Vec2`, `Vec3`, `Quat`. Any other type throws `NotSupportedException` at bind time.

## Notes

- **Read-side convenience only.** `NetworkVariable<T>` never sends anything — it's a typed, change-tracking window over the client `NetworkEntityView` produced by `SetNet.StateSync`. To *change* a field, mutate it on the server (`entity.SetFloat(...)`) and let replication carry it.
- **`Poll()` after `Update()`.** `ClientReplication.Update()` advances interpolation; poll afterward so `Changed` fires against the value your game will actually render this frame. If you never `Poll()`, `Value` still works — you just don't get `Changed` callbacks.
- **Field index and type must match your schema.** `Watch<float>(2)` must line up with the `Float` field you declared at index 2 in the entity's `ReplicaSchema`; a wrong CLR type reads garbage or throws.
- **Interpolated fields change every frame.** For a field declared `interpolate: true`, the value glides between snapshots, so `Changed` fires continuously while it's moving — fine for smooth UI, but don't treat it as a discrete "event". For one-off events tied to an entity, use [`SetNet.StateSync.Rpc`](https://www.nuget.org/packages/SetNet.StateSync.Rpc) instead.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
