<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.MemoryPack

**[MemoryPack](https://github.com/Cysharp/MemoryPack) serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

An extremely fast, zero-encoding, **source-generated** binary serializer with **no runtime reflection** — AOT/IL2CPP-friendly, so it's a strong pick for Unity (often faster and more AOT-robust than MessagePack).

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.MemoryPack
```

```csharp
using SetNet.MemoryPack;
SetNetSerializer.Use(new MemoryPackNetSerializer());   // once, at startup — both ends must match
```

Annotate your message types:

```csharp
[MemoryPackable]
public partial class ChatMessage { public string Text { get; set; } = ""; }
```

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
