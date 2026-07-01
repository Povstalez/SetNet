<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Json

**System.Text.Json serializer for [SetNet](https://www.nuget.org/packages/SetNet).**

A human-readable, web-friendly `ISerializer`. Great for debugging or interop with JSON clients; larger on the wire than a binary format like MessagePack.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Json
```

```csharp
using SetNet.Json;
SetNetSerializer.Use(new JsonNetSerializer());   // once, at startup — both ends must match
```

Pass `JsonSerializerOptions` for custom converters/naming. Your message types are plain POCOs (no `[MessagePackObject]` needed).

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
