<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.MessagePack

**MessagePack serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

The SetNet core library ships serializer-agnostic (it bundles no serializer). This package provides `MessagePackNetSerializer` — an `ISerializer` backed by [MessagePack](https://www.nuget.org/packages/MessagePack) and hardened with the `UntrustedData` security profile (hash-collision protection and recursion-depth limits) to mitigate deserialization denial-of-service on payloads arriving off the network.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.MessagePack
```

## Usage

Register it **once at startup**, before connecting:

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Use(new MessagePackNetSerializer());
```

That's it — SetNet now serializes on send and deserializes on receive (handing your typed handlers the decoded message) through MessagePack.

Your message DTOs must be MessagePack-serializable:

```csharp
[MessagePackObject]
public class ChatMessage
{
    [Key(0)] public string Text { get; set; } = "";
}
```

(or use `[MessagePackObject(true)]` for key-as-name).

> **Unity / IL2CPP / AOT:** MessagePack needs pre-generated formatters on AOT targets (run the `mpc` generator and register the resolver). On Mono/desktop it works out of the box. If AOT setup is a blocker, you can instead supply any other `ISerializer` to `SetNetSerializer.Use(...)`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet
- 📖 [User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)

## License

MIT
