<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.MemoryPack

**[MemoryPack](https://github.com/Cysharp/MemoryPack) serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet ships no serializer in the core — you register one at startup. This package plugs in **MemoryPack**, an extremely fast, zero-encoding, **source-generated** binary serializer with **no runtime reflection**. Because all (de)serialization code is generated at compile time, it's AOT/IL2CPP-friendly, making it a strong default for **Unity** and other trimmed/AOT targets — often faster and more AOT-robust than MessagePack.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.MemoryPack
```

## Usage

Register the serializer **once at startup**, before you create any client or server. Both ends of a connection must use the same serializer.

```csharp
using SetNet.MemoryPack;
using SetNet.Messaging;

SetNetSerializer.Use(new MemoryPackNetSerializer());
```

Annotate every message type with `[MemoryPackable]` and make the type **`partial`** (the source generator emits the serialization code into the other half of the partial):

```csharp
using MemoryPack;

[MemoryPackable]
public partial class ChatMessage
{
    public string User { get; set; } = "";
    public string Text { get; set; } = "";
}
```

That's all — `Serialize<T>` calls `MemoryPackSerializer.Serialize` and `Deserialize<T>` calls `MemoryPackSerializer.Deserialize<T>`.

## Notes

- **`[MemoryPackable] partial` is required.** A non-partial type, or one missing the attribute, won't have generated code and will fail to serialize. This applies to nested/member types too.
- **Speed & size**: among the SetNet serializers this is typically the fastest with the smallest allocations; payloads are compact binary (much smaller than [JSON](https://www.nuget.org/packages/SetNet.Json)).
- **AOT / IL2CPP (Unity)**: source-generated, no runtime reflection or dynamic codegen — the most AOT-robust option here. See the MemoryPack docs for Unity setup and version alignment of the generator.
- **Interop**: MemoryPack's wire format is .NET-specific and not a cross-language standard. If you need to talk to Go/C++/JS clients, use [SetNet.Protobuf](https://www.nuget.org/packages/SetNet.Protobuf) instead.
- Already compact, so [SetNet.Compression](https://www.nuget.org/packages/SetNet.Compression) usually adds little for small messages (its raw-fallback keeps it a no-op there) — it can still help on large state blobs.
- Depends on `MemoryPack` (1.21.1).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
