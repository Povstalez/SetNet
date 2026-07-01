<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Compression

**Transparent Brotli compression for [SetNet](https://www.nuget.org/packages/SetNet).**

A **decorator** `ISerializer`: wrap any inner serializer and payloads get Brotli-compressed on the wire, transparently. Handlers are untouched. A one-byte header means small or incompressible messages ride raw — it never inflates a payload.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Compression
```

```csharp
using SetNet.Compression;
using SetNet.MessagePack;

SetNetSerializer.Use(new CompressingSerializer(new MessagePackNetSerializer(), minBytes: 256));
```

- Wraps **any** inner serializer (MessagePack, JSON, Protobuf…).
- `minBytes` — payloads below this skip compression (default 256).
- Uses **built-in** Brotli (`System.IO.Compression`) — no extra dependency.
- Both ends must use the same wrapper over the same inner serializer.

Best for larger, text-like payloads (JSON, big state). For already-compact binary data the raw-fallback keeps it a no-op.

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
