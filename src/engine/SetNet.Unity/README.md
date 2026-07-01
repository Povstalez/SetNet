<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Unity

**Unity helpers for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet's message handlers and lifecycle callbacks run on **background threads**, but Unity APIs (`Transform`, `GameObject`, UI, …) may only be touched on the **main thread**. This package gives you a tiny main-thread dispatcher to bridge the two.

## Install

Add the DLLs (`SetNet.dll`, `SetNet.Unity.dll`, your serializer) to your Unity project's `Assets/Plugins/`, or reference the package in a package-manager-based project. Requires Unity **2021+** (netstandard2.1). **WebGL is not supported** (no threads/sockets).

## Usage

Drain the queue once per frame from a single MonoBehaviour:

```csharp
using SetNet.Unity;

public class NetDispatcher : MonoBehaviour
{
    void Update() => MainThreadDispatcher.Shared.Drain();
}
```

Marshal handler work onto the main thread:

```csharp
[MessageHandler((ushort)Msg.Move)]
public class MoveHandler : IClientMessageHandler<MoveMessage>
{
    public Task HandleAsync(MoveMessage msg)
    {
        MainThreadDispatcher.Shared.Post(() =>
        {
            // safe: runs on the Unity main thread next frame
            player.transform.position = new Vector3(msg.X, msg.Y, 0);
        });
        return Task.CompletedTask;
    }
}
```

`PostAsync(...)` returns a task you can `await` if a background handler needs main-thread work to finish first.

## IL2CPP / AOT

On IL2CPP/AOT targets (iOS, consoles, Android IL2CPP), MessagePack needs **pre-generated formatters**: run the `mpc` generator for your `[MessagePackObject]` DTOs and register the resolver at startup, or use an AOT-friendly serializer via `SetNetSerializer.Use(...)`. On Mono/desktop it works out of the box.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet

## License

MIT
