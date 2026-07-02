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

- **A thin alias over the unified protocol.** `client.CallAsync<TReq,TResp>(methodId, req)` is exactly `client.RequestAsync<TReq,TResp>(Channels.Rpc, methodId, req)` — the same request/reply mechanism (one envelope, one correlation registry), carried on the reserved `Channels.Rpc` channel with the RPC **method id as the op**. Prefer `RequestAsync` for new code; `CallAsync` + `[RpcMethod]` remain as a typed method-id front end.
- **Serializer-agnostic, no MessagePack dependency** — `SetNet.Rpc` depends only on `SetNet`. The request/response **bodies** go through your `SetNetSerializer` (MessagePack, JSON, …); both ends must use the same serializer.
- No per-package wire ids anymore — RPC rides the single `SetNet.Protocol` envelope (`65447`) like the rest.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet
- 📖 [User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)

## License

MIT
