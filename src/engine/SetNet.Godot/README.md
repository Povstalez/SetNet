<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Godot

**Godot 4 (C#) binding for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet is engine-agnostic, but two things always trip up an engine integration: message handlers run on **background threads** (and Godot's scene tree may only be touched on the main thread), and the core math types (`Vec2`/`Vec3`/`Quat`) aren't Godot's `Vector2`/`Vector3`/`Quaternion`. This package solves both:

- **`GodotMainThreadDispatcher`** — marshal work from SetNet's receive/handler threads onto Godot's main thread, drained once per frame in `_Process`.
- **math conversions** — `ToNet()` / `ToGodot()` extension methods between the `SetNet.StateSync` math types and Godot's vector/quaternion types.

Everything above the transport — handlers, rooms, RPC, state sync — is unchanged; this is just the glue for a Godot 4 C# project.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.Godot
```

Depends on `GodotSharp` and `SetNet.StateSync`. Requires Godot 4's **.NET build** (the C#/Mono edition).

## Usage

SetNet handlers run off the main thread. Enqueue the scene-tree work with the dispatcher inside your handler, then drain it once per frame from a `Node`:

```csharp
using Godot;
using SetNet.Godot;
using SetNet.StateSync;

public partial class NetworkedPawn : Node3D
{
    private Vec3 _latest;   // last replicated position, written from a background thread

    // Called from a StateSync (or message) handler running off the main thread.
    // Never touch a Node here — just stash the data.
    public void OnReplicated(Vec3 position) =>
        GodotMainThreadDispatcher.Shared.Post(() => _latest = position);

    public override void _Process(double delta)
    {
        // run everything queued from background threads, on the main thread:
        GodotMainThreadDispatcher.Shared.Drain();

        // now it's safe to apply the replicated state to the scene tree:
        Position = _latest.ToGodot();
    }
}
```

### Marshalling from a handler

```csharp
// inside a background-thread handler — never touch a Node directly here:
GodotMainThreadDispatcher.Shared.Post(() =>
{
    GetNode<Label>("Hud/Status").Text = "connected";
});

// or await the result on the caller:
await GodotMainThreadDispatcher.Shared.PostAsync(() => SpawnPlayer(id));
```

### Math conversions

```csharp
Vec3 net    = transform.Origin.ToNet();      // Godot Vector3 → core Vec3
Vector3 pos = net.ToGodot();                  // core Vec3 → Godot Vector3

Vec2 v2     = velocity.ToNet();               // Vector2 ↔ Vec2
Quat rot    = Quaternion.Identity.ToNet();    // Quaternion ↔ Quat
```

## API

**`GodotMainThreadDispatcher`:**

| Member | Purpose |
|---|---|
| `static GodotMainThreadDispatcher Shared` | process-wide shared dispatcher |
| `void Post(Action action)` | queue an action to run on the next `Drain()` |
| `Task PostAsync(Action action)` | queue an action; the task completes (or faults) once it has run |
| `void Drain()` | run all queued actions — call once per frame from `_Process(double delta)` |

**`GodotConversions`** (extension methods): `ToNet()` / `ToGodot()` between `Vec2`↔`Vector2`, `Vec3`↔`Vector3`, `Quat`↔`Quaternion`.

## Notes

- **Drain every frame.** If you never call `Drain()`, posted actions queue up and never execute. Call it from a single long-lived `Node`'s `_Process` (e.g. an autoload singleton).
- **`Drain()` isolates faults** — one throwing callback won't stop the rest of the queue that frame.
- **Threading rule.** Only touch the scene tree (nodes, transforms, signals) from the main thread. Do computation in your handler, `Post` the scene-tree write.
- **Web/HTML5 exports** have no threads or raw sockets — pair with [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets) to run over `ws://`.
- Requires Godot 4's .NET build; not for the GDScript-only edition.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
