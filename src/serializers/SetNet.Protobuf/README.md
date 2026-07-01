<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Protobuf

**[protobuf-net](https://github.com/protobuf-net/protobuf-net) serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

Compact binary encoding and — the reason to choose it — **cross-language** interop: talk to non-.NET clients (Go, C++, JS, Python…) that speak Protocol Buffers.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Protobuf
```

```csharp
using SetNet.Protobuf;
SetNetSerializer.Use(new ProtobufNetSerializer());   // once, at startup — both ends must match
```

Annotate your message types:

```csharp
[ProtoContract]
public class ChatMessage { [ProtoMember(1)] public string Text { get; set; } = ""; }
```

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
