using SetNet.Auth;

namespace Auth.Server;

/// <summary>
/// A trivial <see cref="IAuthenticator"/> for the demo: it accepts exactly one token, <c>"letmein"</c>, and rejects
/// everything else. A real implementation would verify a JWT signature, call an account backend, or validate a
/// platform ticket — SetNet.Auth only carries and checks the token; it never issues one.
/// </summary>
public sealed class DemoAuthenticator : IAuthenticator
{
    /// <summary>The one token this demo accepts.</summary>
    public const string ValidToken = "letmein";

    /// <inheritdoc/>
    public Task<AuthResult> AuthenticateAsync(string token)
        => Task.FromResult(token == ValidToken
            ? AuthResult.Ok(accountId: "demo-account")   // resolved identity for a valid token
            : AuthResult.Fail("invalid token"));
}
