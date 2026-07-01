<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auth.Jwt

**JWT-token authenticator for [SetNet.Auth](https://www.nuget.org/packages/SetNet.Auth).**

A drop-in `IAuthenticator` that validates a **JWT bearer token** — signature, issuer, audience, and lifetime — and maps a claim to the account id. Use it when your clients already hold a JWT (issued by your own login service or an identity provider) and you want the SetNet server to gate connections on it. For symmetric (shared-secret / HS256) tokens there's a one-line factory; for full control you pass your own `TokenValidationParameters`.

If your tokens are signed by an OpenID/OAuth provider whose signing keys **rotate**, use [`SetNet.Auth.OAuth`](https://www.nuget.org/packages/SetNet.Auth.OAuth) instead — it fetches the provider's JWKS automatically.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Auth
dotnet add package SetNet.Auth.Jwt
```

## Setup

```csharp
AuthRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

Build the authenticator and hand it to `UseAuth`. `UseAuth` installs the enforced inbound gate from `SetNet.Auth`: until a peer authenticates, its application frames are dropped and only the auth handshake passes.

```csharp
using SetNet.Auth;
using SetNet.Auth.Jwt;

// Symmetric (HS256) tokens signed with a shared secret:
var auth = JwtAuthenticator.WithSymmetricKey(
    secret:       "super-secret-signing-key",
    issuer:       "https://my-auth-service",   // optional; validated only when non-null
    audience:     "setnet-game",               // optional; validated only when non-null
    accountClaim: "sub");                       // claim carrying the account id

server.UseAuth(auth, new AuthOptions());
```

For asymmetric keys (RS256/ES256) or any advanced scenario, use the full constructor with your own `TokenValidationParameters`:

```csharp
var validation = new TokenValidationParameters
{
    IssuerSigningKey         = new RsaSecurityKey(rsaPublicKey),
    ValidateIssuerSigningKey = true,
    ValidIssuer              = "https://my-auth-service",
    ValidateIssuer           = true,
    ValidAudience            = "setnet-game",
    ValidateAudience         = true,
    ValidateLifetime         = true,
};

var auth = new JwtAuthenticator(validation, accountClaim: "sub");
server.UseAuth(auth, new AuthOptions());
```

## Client

Clients present their JWT as the auth token — nothing JWT-specific is needed beyond the base `SetNet.Auth` client hook:

```csharp
// fixed token:
client.UseAuth(myJwt);

// or a provider that returns a fresh JWT on every (re)connect:
var auth = client.UseAuth(tokenProvider: () => accountService.GetFreshJwtAsync());
```

## API

**`JwtAuthenticator : IAuthenticator`**

| Member | Purpose |
|---|---|
| `JwtAuthenticator.WithSymmetricKey(secret, issuer?, audience?, accountClaim = "sub")` | factory for HMAC/HS256 tokens (issuer/audience validated only when non-null; lifetime on; 30 s clock skew) |
| `new JwtAuthenticator(TokenValidationParameters validation, accountClaim = "sub")` | full control over validation (asymmetric keys, custom rules) |
| `Task<AuthResult> AuthenticateAsync(string token)` | called by the server per handshake; returns `AuthResult.Ok(id)` / `AuthResult.Fail(reason)` |

The account id is read from `accountClaim`, falling back to the standard `sub` claim; a missing/empty id fails the token. Any validation error (bad signature, expired, wrong issuer/audience) becomes an `AuthResult.Fail` carrying the exception message.

## Notes

- **Use over TLS.** A JWT is a bearer credential — anyone who sniffs it can replay it. Run SetNet with TLS (TLS-over-TCP, or `wss://` via [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets)) so tokens aren't exposed on the wire.
- **Keep the secret server-side.** For HS256 the same secret verifies and signs — never ship it in a client build. Prefer asymmetric (RS256/ES256) keys when the token is minted elsewhere.
- **Clock skew** is 30 seconds in the `WithSymmetricKey` factory; set `ClockSkew` yourself on the full-control constructor if you need a different tolerance.
- Session resume, multi-session policy, and the session store come from `SetNet.Auth`'s `AuthOptions` — this package only decides *whether a token is valid and who it belongs to*.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
