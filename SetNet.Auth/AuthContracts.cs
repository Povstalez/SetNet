using System;
using System.Threading.Tasks;

namespace SetNet.Auth
{
    /// <summary>
    /// Validates a login token supplied by a connecting client and resolves it to a stable account identity.
    /// You implement it — verify a JWT signature, call your account backend, validate a platform ticket, etc.
    /// SetNet.Auth does not issue tokens; it only carries and checks them on the game connection.
    /// </summary>
    public interface IAuthenticator
    {
        /// <summary>
        /// Validates <paramref name="token"/> and returns the resolved identity or a failure. Should not throw for
        /// an invalid token — return <see cref="AuthResult.Fail"/> instead (exceptions are treated as a failure).
        /// </summary>
        /// <param name="token">The login token the client presented.</param>
        /// <returns>Success with a stable account id, or a failure with a reason.</returns>
        Task<AuthResult> AuthenticateAsync(string token);
    }

    /// <summary>Outcome of validating a login token: success with an account id, or failure with a reason.</summary>
    public sealed class AuthResult
    {
        /// <summary>True if the token was accepted.</summary>
        public bool Success { get; }

        /// <summary>The stable account/player identity (only meaningful when <see cref="Success"/>).</summary>
        public string AccountId { get; }

        /// <summary>The failure reason (only meaningful when not <see cref="Success"/>).</summary>
        public string? Error { get; }

        private AuthResult(bool success, string accountId, string? error)
        {
            Success = success;
            AccountId = accountId;
            Error = error;
        }

        /// <summary>Accepts the token for the given account id.</summary>
        public static AuthResult Ok(string accountId) => new AuthResult(true, accountId ?? "", null);

        /// <summary>Rejects the token with a reason (sent to the caller).</summary>
        public static AuthResult Fail(string error) => new AuthResult(false, "", error ?? "authentication failed");
    }

    /// <summary>The authenticated session a client holds after logging in: who it is and which session it is.</summary>
    public sealed class AuthSession
    {
        /// <summary>The stable account/player id.</summary>
        public string AccountId { get; }

        /// <summary>The per-connection session id (a given account may hold several concurrently).</summary>
        public string SessionId { get; }

        /// <summary>Creates a client-facing session record.</summary>
        public AuthSession(string accountId, string sessionId)
        {
            AccountId = accountId;
            SessionId = sessionId;
        }
    }

    /// <summary>What happens when an account logs in while another session for it is already live.</summary>
    public enum MultiSessionPolicy
    {
        /// <summary>Allow both — each device keeps its own session (default).</summary>
        AllowMultiple = 0,

        /// <summary>Disconnect the existing session(s) for the account; the new login wins.</summary>
        KickExisting = 1,

        /// <summary>Reject the new login while the account already has a live session.</summary>
        RejectNew = 2
    }

    /// <summary>Server-side auth configuration.</summary>
    public sealed class AuthOptions
    {
        /// <summary>Concurrent-session behaviour for one account. Default <see cref="MultiSessionPolicy.AllowMultiple"/>.</summary>
        public MultiSessionPolicy MultiSession { get; set; } = MultiSessionPolicy.AllowMultiple;

        /// <summary>How long a disconnected session stays resumable via its reconnect token. Default 2 minutes.</summary>
        public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(2);
    }

    /// <summary>Thrown on the client when authentication is rejected by the server.</summary>
    public class AuthException : Exception
    {
        /// <summary>Creates an <see cref="AuthException"/> with the server's reason.</summary>
        public AuthException(string message) : base(message) { }
    }
}
