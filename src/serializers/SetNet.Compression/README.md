<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Compression

**Transparent Brotli compression for [SetNet](https://www.nuget.org/packages/SetNet).**

`CompressingSerializer` is a **decorator** `ISerializer`: it wraps *any* inner serializer ([MessagePack](https://www.nuget.org/packages/SetNet.MessagePack), [JSON](https://www.nuget.org/packages/SetNet.Json), [Protobuf](https://www.nuget.org/packages/SetNet.Protobuf), [MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack)) and Brotli-compresses its output on the send path, decompressing on the receive path. Handlers and message types are untouched — you only change the one line where you register the serializer. Use it when payloads are large and compressible (verbose JSON, big state snapshots); for already-compact binary it's a safe no-op.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Compression
# plus whichever inner serializer you wrap, e.g.:
dotnet add package SetNet.MessagePack
```

Brotli comes from the built-in `System.IO.Compression` — this package adds **no extra dependency** of its own.

## Usage

Wrap your inner serializer and register the wrapper **once at startup**, before creating any client or server. Both ends must use the **same wrapper over the same inner serializer**.

```csharp
using SetNet.Compression;
using SetNet.MessagePack;
using SetNet.Messaging;

SetNetSerializer.Use(new CompressingSerializer(new MessagePackNetSerializer()));
```

Constructor:

```csharp
new CompressingSerializer(
    ISerializer inner,                             // required — the serializer to wrap
    int minBytes = 256,                            // payloads smaller than this are sent raw
    CompressionLevel level = CompressionLevel.Fastest)
```

Tune the threshold and level to taste:

```csharp
SetNetSerializer.Use(new CompressingSerializer(
    new JsonNetSerializer(),
    minBytes: 512,
    level: CompressionLevel.Optimal));
```

### How the framing works

Each serialized payload is prefixed with a **one-byte flag**:

- The inner serializer runs first.
- If the result is **smaller than `minBytes`**, it's sent **raw** (flag `0`) — no compression attempted.
- Otherwise it's Brotli-compressed. If compression **doesn't shrink** it (already-compact / incompressible data), it falls back to **raw** (flag `0`) — so the output **never inflates** beyond one byte of overhead.
- On receive, the flag selects raw pass-through or Brotli decompression before handing bytes to the inner serializer.

## Notes

- **Never inflates**: the raw-fallback guarantees at most a single header byte of overhead, even in the worst case.
- **Best for large, text-like payloads** (JSON, large state). For small or already-binary messages the threshold/fallback make it effectively free but rarely a win — set `minBytes` accordingly.
- **CPU trade-off**: compression costs CPU per message. `CompressionLevel.Fastest` (the default) is usually the right balance for realtime traffic; `Optimal` shrinks more at higher cost.
- **Both ends must match**: the receiver has to wrap the *same* inner serializer in `CompressingSerializer`, or it won't decode the flag/format.
- **AOT / IL2CPP (Unity)**: the decorator itself is plain code with no reflection; AOT-friendliness is inherited from whichever inner serializer you wrap.
- No external dependency — Brotli is provided by `System.IO.Compression` in the BCL.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
