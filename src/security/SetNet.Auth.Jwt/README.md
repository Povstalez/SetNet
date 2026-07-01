<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auth.Jwt

**JWT authenticator for [SetNet.Auth](https://www.nuget.org/packages/SetNet.Auth).**

Validate a **JWT** bearer token (signature, issuer, audience, lifetime) and map a claim to the account id.

## Install & use

```bash
dotnet add package SetNet.Auth
dotnet add package SetNet.Auth.Jwt
```

```csharp
using SetNet.Auth.Jwt;

// HMAC (shared secret):
var auth = JwtAuthenticator.WithSymmetricKey("super-secret", issuer: "my-issuer", audience: "my-game");

// or full control (RSA/EC, custom validation):
var auth = new JwtAuthenticator(new TokenValidationParameters { /* ... */ }, accountClaim: "sub");

server.UseAuth(auth, new AuthOptions());
```

Clients present their JWT as the auth token (`client.UseAuth(() => myJwt)`). Use over TLS. For tokens from an OpenID/OAuth provider with rotating keys, use [`SetNet.Auth.OAuth`](https://www.nuget.org/packages/SetNet.Auth.OAuth).

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
