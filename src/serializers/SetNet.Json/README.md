<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Json

**`System.Text.Json` serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

SetNet ships no serializer in the core — you register one at startup. This package plugs in `System.Text.Json`, giving you **human-readable, self-describing** payloads. Reach for it when you want to eyeball traffic while debugging, interop with a browser / non-.NET JSON client, or avoid annotating your DTOs at all. The trade-off is size and speed: JSON is larger on the wire and slower to (de)serialize than a binary format like [MessagePack](https://www.nuget.org/packages/SetNet.MessagePack) or [MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack) — for chatty realtime traffic prefer one of those (optionally behind [SetNet.Compression](https://www.nuget.org/packages/SetNet.Compression)).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Json
```

## Usage

Register the serializer **once at startup**, before you create any client or server. Both ends of a connection must use the same serializer.

```csharp
using SetNet.Json;
using SetNet.Messaging;

SetNetSerializer.Use(new JsonNetSerializer());
```

Your message types are **plain POCOs** — no attributes required:

```csharp
public class ChatMessage
{
    public string User { get; set; } = "";
    public string Text { get; set; } = "";
}
```

Need custom converters, camelCase naming, enums-as-strings, etc.? Pass your own `JsonSerializerOptions`:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
};

SetNetSerializer.Use(new JsonNetSerializer(options));
```

Under the hood, `Serialize<T>` uses `JsonSerializer.SerializeToUtf8Bytes` and `Deserialize<T>` uses `JsonSerializer.Deserialize<T>`, both with your options.

## Notes

- **Payload size**: JSON is text; property names ship with every message. Expect noticeably larger frames than MessagePack/MemoryPack/Protobuf. If size matters, wrap it with [SetNet.Compression](https://www.nuget.org/packages/SetNet.Compression) — text-like JSON compresses very well.
- **Speed**: `System.Text.Json` is fast for a text format, but binary source-generated serializers are faster and allocate less.
- **AOT / IL2CPP (Unity)**: reflection-based `System.Text.Json` can be fragile under aggressive trimming/AOT. For Unity/IL2CPP prefer [SetNet.MemoryPack](https://www.nuget.org/packages/SetNet.MemoryPack) (source-generated, no runtime reflection), or wire up `System.Text.Json` source generation yourself.
- **Interop**: any client that can produce/consume the same JSON shape can talk to a SetNet endpoint using this serializer — but both ends must agree on the exact JSON contract.
- Depends on `System.Text.Json` (8.0.5).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
