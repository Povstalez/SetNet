<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auth.OAuth

**OAuth 2.0 / OpenID Connect authenticator for [SetNet.Auth](https://www.nuget.org/packages/SetNet.Auth).**

Validate access (JWT) tokens issued by a standards-compliant provider — **Auth0, Azure AD/Entra, Keycloak, Google, Cognito**, … — against the provider's published signing keys (JWKS). Keys are fetched from `/.well-known/openid-configuration` and refreshed automatically as they rotate, so you never hard-code keys.

## Install & use

```bash
dotnet add package SetNet.Auth
dotnet add package SetNet.Auth.OAuth
```

```csharp
using SetNet.Auth.OAuth;

var auth = new OpenIdConnectAuthenticator(
    authority: "https://my-tenant.auth0.com/",
    audience:  "my-game-api",
    accountClaim: "sub");

server.UseAuth(auth, new AuthOptions());
```

Clients present their provider access token as the auth token (`client.UseAuth(() => accessToken)`). Use over TLS.

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
