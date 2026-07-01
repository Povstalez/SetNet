using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Auth
{
    /// <summary>
    /// Default in-process <see cref="ISessionStore"/>: sessions resolvable by reconnect token and by account, with
    /// TTL expiry (lazy on resume plus a periodic sweep) and single-use token rotation. Sessions are lost on
    /// restart and not shared across nodes — for that, plug in a Redis/database store via
    /// <see cref="AuthOptions.SessionStore"/>.
    /// </summary>
    public sealed class MemorySessionStore : ISessionStore
    {
        private readonly TimeSpan _ttl;
        private readonly ConcurrentDictionary<string, Session> _byToken = new ConcurrentDictionary<string, Session>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Session>> _byAccount
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, Session>>();

        /// <summary>Creates the store with a session time-to-live (idle window before a disconnected session is no longer resumable).</summary>
        /// <param name="ttl">Session TTL. Defaults to 2 minutes if not positive.</param>
        public MemorySessionStore(TimeSpan ttl = default)
            => _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(2);

        /// <inheritdoc/>
        public Task<Session> CreateAsync(string accountId, PeerInfo? peer)
        {
            var session = new Session(SessionTokens.New(), accountId, SessionTokens.New()) { LivePeer = peer };
            _byToken[session.ReconnectToken] = session;
            _byAccount.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, Session>())[session.SessionId] = session;
            return Task.FromResult(session);
        }

        /// <inheritdoc/>
        public Task<Session?> ResumeAsync(string reconnectToken, PeerInfo? peer)
        {
            if (string.IsNullOrEmpty(reconnectToken) || !_byToken.TryGetValue(reconnectToken, out var session))
                return Task.FromResult<Session?>(null);
            if (DateTime.UtcNow - session.LastSeenUtc > _ttl)
            {
                RemoveInternal(session);
                return Task.FromResult<Session?>(null);
            }

            // Rotate: retire the presented token and issue a new one, so a stolen reconnect token is single-use.
            _byToken.TryRemove(session.ReconnectToken, out _);
            session.ReconnectToken = SessionTokens.New();
            _byToken[session.ReconnectToken] = session;

            session.LastSeenUtc = DateTime.UtcNow;
            session.LivePeer = peer;
            return Task.FromResult<Session?>(session);
        }

        /// <inheritdoc/>
        public Task RemoveAsync(Session session)
        {
            RemoveInternal(session);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyCollection<Session>> SessionsForAccountAsync(string accountId)
            => Task.FromResult<IReadOnlyCollection<Session>>(
                _byAccount.TryGetValue(accountId, out var set) ? set.Values.ToArray() : Array.Empty<Session>());

        /// <inheritdoc/>
        public Task SweepAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _byToken)
                if (now - pair.Value.LastSeenUtc > _ttl)
                    RemoveInternal(pair.Value);
            return Task.CompletedTask;
        }

        private void RemoveInternal(Session session)
        {
            _byToken.TryRemove(session.ReconnectToken, out _);
            if (_byAccount.TryGetValue(session.AccountId, out var set))
                set.TryRemove(session.SessionId, out _);
        }
    }
}
