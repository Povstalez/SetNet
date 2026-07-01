<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Rpc

**Request/response RPC for [SetNet](https://www.nuget.org/packages/SetNet).**

`await client.CallAsync<TRequest, TResponse>(...)` on the client, `[RpcMethod]` handlers on the server — added by **composition**, not inheritance. No `RpcClient`/`RpcPeer` base class: it sits alongside your regular `SendAsync` calls and message handlers. Just reference the package.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.MessagePack   # or your own ISerializer
dotnet add package SetNet.Rpc
```

At startup (once), register your serializer and enable RPC before constructing the client/server:

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Rpc;

SetNetSerializer.Use(new MessagePackNetSerializer());
RpcRuntime.Enable();   // ensures the RPC handlers are discovered
```

## Usage

**Define request/response messages** (serializable by your chosen serializer):

```csharp
public enum Rpc : ushort { Login = 1 }

[MessagePackObject] public class LoginRequest  { [Key(0)] public string User { get; set; } = ""; }
[MessagePackObject] public class LoginResponse { [Key(0)] public bool Ok; [Key(1)] public string Token { get; set; } = ""; }
```

**Server — implement a handler** (auto-discovered, like `[MessageHandler]`):

```csharp
[RpcMethod((ushort)Rpc.Login)]
public class LoginRpc : IRpcHandler<LoginRequest, LoginResponse>
{
    public Task<LoginResponse> HandleAsync(BasePeer peer, LoginRequest req)
        => Task.FromResult(new LoginResponse { Ok = true, Token = "..." });
}
```

**Client — call it and await the response:**

```csharp
var reply = await client.CallAsync<LoginRequest, LoginResponse>(
    (ushort)Rpc.Login,
    new LoginRequest { User = "alice" },
    timeoutMs: 5000);
```

- A server-side exception is relayed and re-thrown on the caller as `RpcException`.
- No response within the timeout throws `TimeoutException`; a `CancellationToken` is honored.
- Your existing one-way messages (`SendAsync` + `[MessageHandler]`) keep working unchanged.

## Notes

- Uses reserved wire type ids `65531`/`65532` for its envelopes — don't use those for your own messages.
- The RPC **body** is serialized with your app's `SetNetSerializer` (MessagePack, JSON, …); only the thin envelope is fixed.
- Both ends of a call must use the same serializer.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet
- 📖 [User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)

## License

MIT
