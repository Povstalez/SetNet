<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet StateSync for Unity

**Unity components for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync) — server-authoritative entity replication with rich per-object component sync.**

Drop `NetworkObject` on a prefab, add the sync components you want (Transform, Animator, Rigidbody, or your own), assign the prefab to a `NetworkManager`, and the same prefab replicates from a dedicated server to every client — with client-side interpolation, delta compression, interest management, and an input channel — all handled by the engine-agnostic core.

> This is a **Unity (UPM) source package**, not a NuGet package: it references `UnityEngine`. The networking itself (`SetNet`, `SetNet.StateSync`, `SetNet.Unity`) are plain .NET assemblies you add to the Unity project.

## What it syncs

| Component | Replicates |
|---|---|
| **NetworkTransform** | position + rotation (interpolated), optional scale, local/world space, optional position quantization, optional owner-authoritative (prediction) |
| **NetworkAnimator** | every controller parameter — Float (interpolated), Int, Bool, and Trigger (as a pulse counter) — plus optional per-layer state hash + normalized time so a missed trigger still converges |
| **NetworkRigidbody** | linear + angular velocity; makes non-owner clients kinematic so the network drives the transform cleanly |
| **NetworkBehaviour** | your own fields — subclass and declare/serialize/deserialize them (AOT-safe, no reflection) |

An object's schema is the **ordered concatenation** of its components' fields, built from the shared prefab — so the server and clients always agree.

## Install

1. Add the SetNet assemblies to your Unity project (via [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity): `SetNet`, `SetNet.MessagePack` — or your serializer —, `SetNet.StateSync`, `SetNet.Unity`; plus any transport package like `SetNet.WebSockets`).
2. Add this package via UPM (Package Manager → *Add package from git URL*) pointing at `…/SetNet.StateSync.Unity`, or copy its `Runtime/` folder into your project.

## Setup

**1. Build a networked prefab:** add `NetworkObject` (set a unique `Archetype Id`) + `NetworkTransform` + `NetworkAnimator` etc.

**2. Register your serializer + transport once at startup:**

```csharp
SetNetSerializer.Use(new MessagePackNetSerializer());
```

**3. Server** (dedicated build or host):

```csharp
var config = new Configuration { Host = "0.0.0.0", Port = 5000 };   // or .UseWebSockets()
var server = new MyServer(config);
await server.StartAsync();

networkManager.StartServer(server, new StateSyncOptions { TickRate = 30 });
var player = networkManager.ServerSpawn(archetype: 1, spawnPos, Quaternion.identity, owner: peerId);
```

**4. Client:**

```csharp
var client = new MyClient(config);
networkManager.StartClient(client, new StateSyncOptions { InterpolationDelayMs = 100 });
await client.ConnectAsync();
// NetworkManager instantiates/destroys prefabs automatically as entities enter/leave your view.
```

`NetworkManager.Update()` drains spawns/despawns onto the main thread, pushes server object state each frame, and advances client interpolation — no per-object glue needed.

## Prediction

Set `NetworkTransform.ownerAuthoritative` on the player prefab and drive the owned object locally; send inputs with `networkManager.Client.SendInput(bytes)` and reconcile against `Client.LastProcessedInput`. The core carries the input channel and the last-processed-input echo; the rewind/replay policy is game-specific.

## AOT / IL2CPP / WebGL

- **IL2CPP/AOT:** the components declare their fields explicitly (no runtime reflection), so replication is AOT-safe. Your serializer still needs AOT setup — MessagePack needs generated resolvers, or use an AOT-friendly `ISerializer`.
- **Main thread:** spawn/despawn and state application run on Unity's main thread via `SetNet.Unity.MainThreadDispatcher` (drained in `NetworkManager.Update`).
- **WebGL:** no UDP/threads in the browser — use `SetNet.WebSockets` (reliable-only); interpolation and prediction still work.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
