using System;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace SetNet.Auth.Jwt
{
    /// <summary>
    /// An <see cref="IAuthenticator"/> that validates a **JWT** bearer token (signature, issuer, audience, lifetime) and
    /// maps a claim to the account id. Plug it into <c>server.UseAuth(new JwtAuthenticator(...), options)</c> and have
    /// clients present their JWT as the auth token. For tokens signed by an OpenID/OAuth provider whose keys rotate, use
    /// <c>SetNet.Auth.OAuth</c> instead (it fetches the provider's JWKS automatically).
    /// </summary>
    public sealed class JwtAuthenticator : IAuthenticator
    {
        private readonly TokenValidationParameters _validation;
        private readonly string _accountClaim;
        private readonly JwtSecurityTokenHandler _handler = new JwtSecurityTokenHandler();

        /// <summary>Creates the authenticator with full validation parameters and the claim carrying the account id (default <c>sub</c>).</summary>
        public JwtAuthenticator(TokenValidationParameters validation, string accountClaim = "sub")
        {
            _validation = validation ?? throw new ArgumentNullException(nameof(validation));
            _accountClaim = accountClaim;
        }

        /// <summary>Convenience factory for HMAC (HS256) tokens signed with a shared secret.</summary>
        public static JwtAuthenticator WithSymmetricKey(string secret, string? issuer = null, string? audience = null, string accountClaim = "sub")
        {
            var validation = new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ValidateIssuerSigningKey = true,
                ValidateIssuer = issuer != null,
                ValidIssuer = issuer,
                ValidateAudience = audience != null,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            return new JwtAuthenticator(validation, accountClaim);
        }

        /// <inheritdoc/>
        public Task<AuthResult> AuthenticateAsync(string token)
        {
            try
            {
                var principal = _handler.ValidateToken(token, _validation, out _);
                var id = principal.FindFirst(_accountClaim)?.Value ?? principal.FindFirst("sub")?.Value;
                return Task.FromResult(string.IsNullOrEmpty(id)
                    ? AuthResult.Fail($"token has no '{_accountClaim}' claim")
                    : AuthResult.Ok(id!));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AuthResult.Fail(ex.Message));
            }
        }
    }
}
