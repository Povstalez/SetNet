<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auth.OAuth

**OAuth 2.0 / OpenID Connect authenticator for [SetNet.Auth](https://www.nuget.org/packages/SetNet.Auth).**

An `IAuthenticator` that validates access (JWT) tokens issued by a standards-compliant identity provider — **Auth0, Azure AD / Entra ID, Keycloak, Google, Cognito**, and friends — against the provider's *published* signing keys. Keys are discovered from the authority's `/.well-known/openid-configuration` (JWKS) and **refreshed automatically as they rotate**, so you never hard-code or manually rotate keys. Use this when your clients already log in through an external provider and hand SetNet the resulting access token.

For tokens you sign yourself with a static key (shared secret or your own RSA key), use [`SetNet.Auth.Jwt`](https://www.nuget.org/packages/SetNet.Auth.Jwt) instead.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Auth
dotnet add package SetNet.Auth.OAuth
```

## Setup

```csharp
AuthRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

Point the authenticator at your provider's authority and hand it to `UseAuth`. `UseAuth` installs the enforced inbound gate from `SetNet.Auth`: until a peer authenticates, its application frames are dropped and only the auth handshake passes.

```csharp
using SetNet.Auth;
using SetNet.Auth.OAuth;

var auth = new OpenIdConnectAuthenticator(
    authority:    "https://my-tenant.auth0.com/",   // its /.well-known/openid-configuration is fetched
    audience:     "my-game-api",                     // expected `aud`; null to skip audience checks
    accountClaim: "sub");                             // claim carrying the account id

server.UseAuth(auth, new AuthOptions());
```

Provider authority examples:

| Provider | Authority |
|---|---|
| Auth0 | `https://my-tenant.auth0.com/` |
| Azure AD / Entra | `https://login.microsoftonline.com/{tenant}/v2.0` |
| Keycloak | `https://kc.example.com/realms/{realm}` |
| Google | `https://accounts.google.com` |
| AWS Cognito | `https://cognito-idp.{region}.amazonaws.com/{poolId}` |

## Client

Clients present the provider's access token as their auth token — no OAuth-specific code beyond the base `SetNet.Auth` client hook:

```csharp
// a provider that returns a fresh access token on every (re)connect:
var auth = client.UseAuth(tokenProvider: () => identityClient.GetAccessTokenAsync());

// or a fixed token you already obtained:
client.UseAuth(accessToken);
```

## API

**`OpenIdConnectAuthenticator : IAuthenticator`**

| Member | Purpose |
|---|---|
| `new OpenIdConnectAuthenticator(authority, audience? = null, accountClaim = "sub")` | build from a provider authority URL |
| `Task<AuthResult> AuthenticateAsync(string token)` | called by the server per handshake; validates against the cached JWKS |

Validation checks signing key, issuer (the authority's own issuer), audience (when `audience` is non-null), and lifetime, with a 30-second clock skew. The account id comes from `accountClaim`, falling back to `sub`; a missing id fails the token. The discovery document + JWKS are fetched once and cached by a `ConfigurationManager`, which refreshes them on its own schedule as keys rotate.

## Notes

- **Use over TLS.** The access token is a bearer credential — sniffable and replayable if the connection isn't encrypted. Run SetNet with TLS-over-TCP or `wss://` (via [`SetNet.WebSockets`](https://www.nuget.org/packages/SetNet.WebSockets)).
- **Outbound access required.** The server must be able to reach the authority's `/.well-known/openid-configuration` and JWKS endpoints to validate tokens.
- **Audience matters.** Validate `audience` in production so tokens minted for a *different* app of the same tenant can't be replayed against your server; pass `null` only if you deliberately accept any audience.
- Session resume, multi-session policy, and the session store come from `SetNet.Auth`'s `AuthOptions` — this package only decides *whether a token is valid and who it belongs to*.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
