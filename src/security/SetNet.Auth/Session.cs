using System;
using SetNet.Config;

namespace SetNet.Auth
{
    /// <summary>
    /// One authenticated session: a stable account identity, a reconnect token (single-use, rotated on resume),
    /// last-activity time, and the currently live connection (runtime-only, used for policy kicks). A custom
    /// <see cref="ISessionStore"/> persists everything except <see cref="LivePeer"/>.
    /// </summary>
    public sealed class Session
    {
        /// <summary>Stable per-session id (an account may hold several concurrently).</summary>
        public string SessionId { get; }

        /// <summary>The account/player identity this session belongs to.</summary>
        public string AccountId { get; }

        /// <summary>The current reconnect token; rotate it on each resume so a captured token is single-use.</summary>
        public string ReconnectToken { get; set; }

        /// <summary>Last activity (UTC), used for TTL expiry.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>The connection currently backing this session (for policy kicks); not persisted, may be stale after a drop.</summary>
        public PeerInfo? LivePeer { get; set; }

        /// <summary>Creates a session record.</summary>
        public Session(string sessionId, string accountId, string reconnectToken)
        {
            SessionId = sessionId;
            AccountId = accountId;
            ReconnectToken = reconnectToken;
            LastSeenUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Helper for generating unguessable session/reconnect tokens (reuse it in custom stores).</summary>
    public static class SessionTokens
    {
        /// <summary>A fresh random token (128-bit, hex).</summary>
        public static string New() => Guid.NewGuid().ToString("N");
    }
}
