# SetNet.Godot

**Godot 4 (C#) helpers for [SetNet](https://www.nuget.org/packages/SetNet).**

Two things you need when running SetNet inside Godot:

- **`GodotMainThreadDispatcher`** — SetNet handlers run on background threads, but Godot's scene tree must only be touched on the main thread. Enqueue from a handler, drain once per frame.
- **Math conversions** — between `SetNet.StateSync`'s `Vec2/Vec3/Quat` and Godot's `Vector2/Vector3/Quaternion`, for replicated transforms.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.Godot
```

```csharp
using SetNet.Godot;

public partial class Net : Node
{
    public override void _Process(double delta)
    {
        GodotMainThreadDispatcher.Shared.Drain();   // run queued handler work on the main thread
        _replication?.Update();                      // advance StateSync interpolation
    }
}

// in a background handler:
GodotMainThreadDispatcher.Shared.Post(() => GetNode<Node3D>("Enemy").Position = view.GetVec3(0).ToGodot());
```

Requires Godot 4's .NET (C#) build. Works with any SetNet transport; for HTML5/Web exports use [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets).

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
