using System;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SetNet.Auth.OAuth
{
    /// <summary>
    /// An <see cref="IAuthenticator"/> that validates an **OAuth 2.0 / OpenID Connect** access (JWT) token against a
    /// provider's published signing keys. It fetches and caches the provider's discovery document + JWKS
    /// (<c>/.well-known/openid-configuration</c>) and refreshes them as keys rotate — so you validate tokens issued by
    /// Auth0, Azure AD/Entra, Keycloak, Google, Cognito, etc. without hard-coding keys. Maps a claim to the account id.
    /// </summary>
    public sealed class OpenIdConnectAuthenticator : IAuthenticator
    {
        private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;
        private readonly string? _audience;
        private readonly string _accountClaim;
        private readonly JwtSecurityTokenHandler _handler = new JwtSecurityTokenHandler();

        /// <summary>
        /// Creates the authenticator for a provider authority (e.g. <c>https://my-tenant.auth0.com/</c> or
        /// <c>https://login.microsoftonline.com/{tenant}/v2.0</c>).
        /// </summary>
        /// <param name="authority">The OIDC authority base URL; its <c>/.well-known/openid-configuration</c> is used.</param>
        /// <param name="audience">Expected audience (<c>aud</c>); null to skip audience validation.</param>
        /// <param name="accountClaim">Claim carrying the account id (default <c>sub</c>).</param>
        public OpenIdConnectAuthenticator(string authority, string? audience = null, string accountClaim = "sub")
        {
            if (string.IsNullOrEmpty(authority)) throw new ArgumentNullException(nameof(authority));
            var metadata = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever());
            _audience = audience;
            _accountClaim = accountClaim;
        }

        /// <inheritdoc/>
        public async Task<AuthResult> AuthenticateAsync(string token)
        {
            try
            {
                var oidc = await _configManager.GetConfigurationAsync().ConfigureAwait(false);
                var validation = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = oidc.SigningKeys,
                    ValidateIssuer = true,
                    ValidIssuer = oidc.Issuer,
                    ValidateAudience = _audience != null,
                    ValidAudience = _audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                var principal = _handler.ValidateToken(token, validation, out _);
                var id = principal.FindFirst(_accountClaim)?.Value ?? principal.FindFirst("sub")?.Value;
                return string.IsNullOrEmpty(id)
                    ? AuthResult.Fail($"token has no '{_accountClaim}' claim")
                    : AuthResult.Ok(id!);
            }
            catch (Exception ex)
            {
                return AuthResult.Fail(ex.Message);
            }
        }
    }
}
