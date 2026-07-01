<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auth

**Authentication & sessions for [SetNet](https://www.nuget.org/packages/SetNet) — by composition, no base class.**

Plug it in and, until a peer authenticates, **all of its application frames (regular messages and RPC) are dropped** — only the auth handshake gets through. You validate the token; the package manages sessions, the enforced gate, and automatic reconnect-resume. Without this package, SetNet works fully open, as before.

> 🔒 **Use over TLS** (`UseSsl = true`) so tokens aren't sent in the clear.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.MessagePack   # or your own ISerializer
dotnet add package SetNet.Auth
```

At startup (once), before constructing your client/server:

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;
using SetNet.Auth;

SetNetSerializer.Use(new MessagePackNetSerializer());
AuthRuntime.Enable();   // ensures the auth handlers are discovered
```

## Where does the token come from?

**Not from SetNet.** Your account/auth backend issues it out-of-band (HTTP login, OAuth, Steam/Apple/Google ticket, a guest token, …). The client presents that token; the server validates it via your `IAuthenticator`.

## Server

Implement `IAuthenticator` (verify a JWT, call your backend, …) and enable auth on the server:

```csharp
public class MyAuthenticator : IAuthenticator
{
    public Task<AuthResult> AuthenticateAsync(string token)
    {
        // validate however you like:
        if (TokenIsValid(token, out var accountId)) return Task.FromResult(AuthResult.Ok(accountId));
        return Task.FromResult(AuthResult.Fail("invalid token"));
    }
}

var server = new MyServer(config);
server.UseAuth(new MyAuthenticator(), new AuthOptions
{
    MultiSession = MultiSessionPolicy.AllowMultiple,   // or KickExisting / RejectNew
    SessionTtl   = TimeSpan.FromMinutes(2)             // reconnect window
});
await server.StartAsync();
```

Inside your handlers, the peer is already authenticated (unauthenticated traffic never reaches them).

## Client

Attach auth **before** connecting; it authenticates automatically on connect **and** every reconnect:

```csharp
var client = new MyClient(config);
var auth = client.UseAuth(tokenProvider: () => accountService.GetFreshTokenAsync());
// (or a fixed token: client.UseAuth("my-token"))

await client.ConnectAsync();
var session = await auth.WhenAuthenticated;   // throws AuthException if rejected
Console.WriteLine($"Logged in as {session.AccountId}");

// now send normally — the gate is open
await client.SayAsync("hi");
```

- `auth.IsAuthenticated`, `auth.Session`, and the `Authenticated` / `AuthFailed` events are available too.

## Reconnect & sessions

- After login the server issues a **reconnect token**; the client stores it and, on reconnect, **resumes the same session** automatically (within `SessionTtl`).
- The reconnect token **rotates on every resume** (single-use), so a captured token is short-lived; the client updates to the new one transparently.
- Idle sessions are evicted by a **background sweep** once past `SessionTtl`, so dead sessions don't accumulate.
- If the session has expired, the client falls back to a **fresh login** via your `tokenProvider` (which can return a refreshed token) — also automatic.
- **Multi-session:** the same account on two devices = two sessions by default (`AllowMultiple`). Use `KickExisting` to disconnect the old device, or `RejectNew` to refuse the second login. Reconnect is always per-session, not per-account.

## Notes

- **Serializer-agnostic, depends only on `SetNet`** — the handshake is hand-framed (no MessagePack dependency).
- Uses reserved wire type ids `65529`/`65530` — don't use those for your own messages.
- The gate never blocks heartbeat/system frames.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet
- 📖 [User guide](https://github.com/Povstalez/SetNet/blob/master/docs/GUIDE.en.md)

## License

MIT
