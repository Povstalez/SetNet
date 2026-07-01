using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Auth
{
    /// <summary>
    /// Persistence for auth sessions. The default is <see cref="MemorySessionStore"/> (in-process); supply your own
    /// (Redis, a database, …) via <see cref="AuthOptions.SessionStore"/> to survive restarts or share sessions
    /// across a server cluster. Methods are async so a backing store can do I/O. Implementations must be thread-safe.
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>Creates and persists a fresh session for a just-authenticated account.</summary>
        Task<Session> CreateAsync(string accountId, PeerInfo? peer);

        /// <summary>
        /// Resolves a session by reconnect token, re-binding it to <paramref name="peer"/> and <b>rotating</b> the
        /// token (the presented one becomes invalid; the returned session carries a fresh one). Returns null if the
        /// token is unknown or the session has expired.
        /// </summary>
        Task<Session?> ResumeAsync(string reconnectToken, PeerInfo? peer);

        /// <summary>Removes a session (e.g. when a multi-session policy kicks it).</summary>
        Task RemoveAsync(Session session);

        /// <summary>All currently stored sessions for an account (for applying <see cref="MultiSessionPolicy"/>).</summary>
        Task<IReadOnlyCollection<Session>> SessionsForAccountAsync(string accountId);

        /// <summary>Evicts sessions idle past their TTL. Called periodically by the background sweep; a no-op for stores that auto-expire (e.g. Redis).</summary>
        Task SweepAsync();
    }
}
