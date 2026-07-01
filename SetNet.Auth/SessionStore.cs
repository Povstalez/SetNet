using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SetNet.Config;

namespace SetNet.Auth
{
    /// <summary>One server-side session: a stable account identity, a rotating-capable reconnect token, and the currently live connection (if any).</summary>
    internal sealed class Session
    {
        public string SessionId { get; }
        public string AccountId { get; }
        public string ReconnectToken { get; }
        public DateTime LastSeenUtc { get; set; }

        /// <summary>The connection currently backing this session, for policy kicks; may be stale after a drop (Disconnect is idempotent).</summary>
        public PeerInfo? LivePeer { get; set; }

        public Session(string sessionId, string accountId, string reconnectToken)
        {
            SessionId = sessionId;
            AccountId = accountId;
            ReconnectToken = reconnectToken;
            LastSeenUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// In-memory session registry: resolvable by reconnect token (for resume) and by account (for multi-session
    /// policy). Disconnected sessions stay resumable until their TTL lapses (lazily evicted on lookup).
    /// </summary>
    internal sealed class SessionStore
    {
        private readonly TimeSpan _ttl;
        private readonly ConcurrentDictionary<string, Session> _byToken = new ConcurrentDictionary<string, Session>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Session>> _byAccount
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, Session>>();

        public SessionStore(TimeSpan ttl) => _ttl = ttl;

        private static string NewToken() => Guid.NewGuid().ToString("N");

        /// <summary>Creates and stores a fresh session for a just-authenticated account.</summary>
        public Session Create(string accountId, PeerInfo peer)
        {
            var session = new Session(NewToken(), accountId, NewToken()) { LivePeer = peer };
            _byToken[session.ReconnectToken] = session;
            _byAccount.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, Session>())[session.SessionId] = session;
            return session;
        }

        /// <summary>Looks up a session by reconnect token, re-binding it to <paramref name="peer"/>. Returns null if unknown or expired (evicting the latter).</summary>
        public Session? Resume(string reconnectToken, PeerInfo peer)
        {
            if (string.IsNullOrEmpty(reconnectToken) || !_byToken.TryGetValue(reconnectToken, out var session))
                return null;
            if (DateTime.UtcNow - session.LastSeenUtc > _ttl)
            {
                Remove(session);
                return null;
            }
            session.LastSeenUtc = DateTime.UtcNow;
            session.LivePeer = peer;
            return session;
        }

        /// <summary>Refreshes a session's activity timestamp.</summary>
        public void Touch(Session session) => session.LastSeenUtc = DateTime.UtcNow;

        /// <summary>Removes a session from both indexes.</summary>
        public void Remove(Session session)
        {
            _byToken.TryRemove(session.ReconnectToken, out _);
            if (_byAccount.TryGetValue(session.AccountId, out var set))
                set.TryRemove(session.SessionId, out _);
        }

        /// <summary>All currently stored sessions for an account (for applying <see cref="MultiSessionPolicy"/>).</summary>
        public IReadOnlyCollection<Session> SessionsForAccount(string accountId)
            => _byAccount.TryGetValue(accountId, out var set) ? set.Values.ToArray() : Array.Empty<Session>();
    }
}
