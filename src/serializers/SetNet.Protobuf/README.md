<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Protobuf

**[protobuf-net](https://github.com/protobuf-net/protobuf-net) serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet ships no serializer in the core — you register one at startup. This package plugs in **protobuf-net** (Protocol Buffers). It produces compact binary payloads, but the real reason to pick it is **cross-language interop**: a SetNet server can exchange messages with non-.NET clients (Go, C++, JavaScript, Python, …) that speak protobuf, as long as both sides share the same field-numbered contract. If everything is .NET, [MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack) or [MessagePack](https://www.nuget.org/packages/SetNet.MessagePack) are usually faster.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Protobuf
```

## Usage

Register the serializer **once at startup**, before you create any client or server. Both ends of a connection must use the same serializer.

```csharp
using SetNet.Protobuf;
using SetNet.Messaging;

SetNetSerializer.Use(new ProtobufNetSerializer());
```

Annotate each message type with `[ProtoContract]` and give every serialized member a **stable** `[ProtoMember(n)]` tag:

```csharp
using ProtoBuf;

[ProtoContract]
public class ChatMessage
{
    [ProtoMember(1)] public string User { get; set; } = "";
    [ProtoMember(2)] public string Text { get; set; } = "";
}
```

`Serialize<T>` writes to a `MemoryStream` via `ProtoBuf.Serializer.Serialize`; `Deserialize<T>` reads it back with `ProtoBuf.Serializer.Deserialize<T>`.

## Notes

- **Field numbers are the contract.** The `n` in `[ProtoMember(n)]` — not the property name — identifies a field on the wire. Keep tags stable across versions; you can add new tags for forward/backward compatibility, but don't reuse or renumber existing ones.
- **Cross-language**: the payload is standard Protocol Buffers, so a matching `.proto`/schema on the other side lets non-.NET clients interop. (Both ends still must agree on field numbers and types.)
- **Size & speed**: compact and fast — smaller than [JSON](https://www.nuget.org/packages/SetNet.Json); typically a bit slower and larger than [MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack) for pure-.NET workloads.
- **AOT / IL2CPP (Unity)**: protobuf-net uses runtime metadata/codegen, which can need extra care under IL2CPP/aggressive trimming. For Unity, [SetNet.MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack) is the more AOT-robust default unless you specifically need protobuf interop.
- Depends on `protobuf-net` (3.2.30).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
