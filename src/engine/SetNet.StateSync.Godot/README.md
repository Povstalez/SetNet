<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.Godot

**Godot 4 (C#) components for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync) — server-authoritative entity replication in the Godot scene tree.**

Make a scene whose root is a `NetworkObject`, add the sync components you want (Transform, AnimationPlayer, RigidBody, or your own), register the scene with a `NetworkManager`, and it replicates from a dedicated server to every client — with client-side interpolation, delta compression, interest management and an input channel — all handled by the engine-agnostic core. This is the Godot analog of `SetNet.StateSync.Unity`.

## What it syncs

| Component | Replicates |
|---|---|
| **NetworkTransform** | position + rotation (interpolated), optional scale, optional position quantization, optional owner-authoritative (prediction) |
| **NetworkAnimationPlayer** | the `AnimationPlayer`'s current animation + playback position (non-owners `Play` + `Seek` to follow) |
| **NetworkRigidBody** | a `RigidBody3D`'s linear + angular velocity |
| **NetworkBehaviour** | your own fields — subclass and declare/serialize/deserialize them (AOT-safe, no reflection) |

An object's schema is the **ordered concatenation** of its child components' fields, built from the shared scene — so server and clients always agree.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.Godot
dotnet add package SetNet.StateSync.Godot
```

Requires Godot 4's **.NET (C#) build**.

> **NuGet vs. source.** Referenced as a NuGet package these components work **code-driven** (create/configure nodes in C#). For **editor/scene-driven** use — `[GlobalClass]` node types in the *Create Node* dialog and `[Export]` fields in the inspector — Godot's source generators must run on *your* project, so add this package's `.cs` files to your Godot project as an addon instead of (or alongside) the NuGet reference.

## Setup

**1. Build a networked scene:** root = `NetworkObject` (set a unique `Archetype Id`) with child nodes `NetworkTransform`, `NetworkAnimationPlayer`, etc. Save it as a `.tscn`.

**2. A `NetworkManager` node** with the scene(s) in its `Registered Scenes`.

**3. Register a serializer once at startup:**

```csharp
SetNetSerializer.Use(new MessagePackNetSerializer());
```

**4. Server** (dedicated build or host):

```csharp
var server = new MyServer(new Configuration { Host = "0.0.0.0", Port = 5000 });
await server.StartAsync();

networkManager.StartServer(server, new StateSyncOptions { TickRate = 30 });
var player = networkManager.ServerSpawn(archetype: 1, spawnPos, Quaternion.Identity, owner: peerId);
```

**5. Client:**

```csharp
var client = new MyClient(new Configuration { Host = "server-ip", Port = 5000 });   // or .UseWebSockets()
networkManager.StartClient(client, new StateSyncOptions { InterpolationDelayMs = 100 });
await client.ConnectAsync();
// NetworkManager instantiates/frees scenes automatically as entities enter/leave your view.
```

`NetworkManager._Process` drains spawns/despawns onto Godot's main thread (via `SetNet.Godot`'s dispatcher), pushes server object state each frame, and advances client interpolation — no per-object glue needed.

## Structure

Because Godot uses inheritance for node types, a networked object is a **`NetworkObject` (Node3D) root** with **child** sync-component nodes (rather than Unity-style added components). `NetworkTransform` defaults to syncing its parent `NetworkObject`; `NetworkRigidBody`/`NetworkAnimationPlayer` auto-find (or you `[Export]`) the `RigidBody3D`/`AnimationPlayer` to drive.

## Prediction

Set `NetworkTransform.OwnerAuthoritative` on the player scene and drive the owned object locally; send inputs with `networkManager.Client.SendInput(bytes)` and reconcile against `Client.LastProcessedInput` (see [`SetNet.StateSync.Prediction`](https://www.nuget.org/packages/SetNet.StateSync.Prediction)).

## Notes

- **3D-focused** (v1): `NetworkObject` is a `Node3D`. A 2D binding would mirror this with `Node2D`.
- **Main thread:** spawn/despawn and state application run on Godot's main thread via `SetNet.Godot.GodotMainThreadDispatcher`, drained in `NetworkManager._Process`.
- **Web/HTML5 exports** have no threads/UDP — use [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
