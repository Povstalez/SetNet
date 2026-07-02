using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SetNet.Auth;
using SetNet.BanList;
using SetNet.Config;
using SetNet.Rooms;
using StackExchange.Redis;

namespace SetNet.Redis
{
    /// <summary>
    /// Redis-backed implementations of SetNet's pluggable stores, so authentication sessions, bans and room codes are
    /// <b>shared across every server node</b> and <b>survive restarts</b> instead of living in one process's memory.
    /// Plug them into the modules that own each seam:
    /// <list type="bullet">
    /// <item><see cref="RedisSessionStore"/> → <c>AuthOptions.SessionStore</c> (SetNet.Auth)</item>
    /// <item><see cref="RedisBanStore"/> → <c>server.UseBanList(store)</c> (SetNet.BanList)</item>
    /// <item><see cref="RedisRoomStore"/> → <c>server.UseRooms(store)</c> / <c>server.UseMatchmaking(store, …)</c> (SetNet.Rooms)</item>
    /// </list>
    /// Share one <see cref="IConnectionMultiplexer"/> across all of them.
    /// </summary>
    public static class RedisStores
    {
        /// <summary>Opens a Redis connection (e.g. <c>"localhost:6379"</c> or a full StackExchange.Redis configuration string).</summary>
        public static IConnectionMultiplexer Connect(string configuration) => ConnectionMultiplexer.Connect(configuration);
    }

    /// <summary>Redis-backed <see cref="IBanStore"/>: bans are shared across nodes; timed bans use Redis key TTL (auto-expire).</summary>
    public sealed class RedisBanStore : IBanStore
    {
        private readonly IConnectionMultiplexer _mux;
        private readonly string _prefix;

        /// <summary>Creates the store over a shared multiplexer; <paramref name="prefix"/> namespaces the keys.</summary>
        public RedisBanStore(IConnectionMultiplexer mux, string prefix = "setnet:")
        {
            _mux = mux ?? throw new ArgumentNullException(nameof(mux));
            _prefix = prefix;
        }

        private IDatabase Db => _mux.GetDatabase();
        private string Key(string key) => $"{_prefix}ban:{key}";

        /// <inheritdoc/>
        public bool IsBanned(string key) => Db.KeyExists(Key(key));

        /// <inheritdoc/>
        public void Ban(string key, DateTime? untilUtc = null)
        {
            if (untilUtc == null) { Db.StringSet(Key(key), "1"); return; }
            var ttl = untilUtc.Value - DateTime.UtcNow;
            if (ttl <= TimeSpan.Zero) Db.KeyDelete(Key(key));   // already expired → treat as not banned
            else Db.StringSet(Key(key), "1", ttl);
        }

        /// <inheritdoc/>
        public void Unban(string key) => Db.KeyDelete(Key(key));
    }

    /// <summary>
    /// Redis-backed <see cref="ISessionStore"/>: sessions and their reconnect tokens live in Redis with a TTL, so a
    /// client can resume its session on <b>any</b> node and after a server restart. Token rotation is preserved.
    /// <para>Note: <c>Session.LivePeer</c> is a runtime-only reference to a live connection and is never persisted, so
    /// the <c>KickExisting</c> multi-session policy can only kick a session whose live connection is on the current
    /// node — cross-node kicks require a side channel (e.g. <see cref="T:SetNet.Cluster.ClusterNode"/>).</para>
    /// </summary>
    public sealed class RedisSessionStore : ISessionStore
    {
        private readonly IConnectionMultiplexer _mux;
        private readonly TimeSpan _ttl;
        private readonly string _prefix;

        /// <summary>Creates the store; <paramref name="ttl"/> is the idle window before a session is no longer resumable (default 2 min).</summary>
        public RedisSessionStore(IConnectionMultiplexer mux, TimeSpan ttl = default, string prefix = "setnet:")
        {
            _mux = mux ?? throw new ArgumentNullException(nameof(mux));
            _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(2);
            _prefix = prefix;
        }

        private IDatabase Db => _mux.GetDatabase();
        private string SessKey(string id) => $"{_prefix}sess:{id}";
        private string TokenKey(string token) => $"{_prefix}sesstoken:{token}";
        private string AccountKey(string accountId) => $"{_prefix}sessacct:{accountId}";

        /// <inheritdoc/>
        public async Task<Session> CreateAsync(string accountId, PeerInfo? peer)
        {
            var session = new Session(SessionTokens.New(), accountId, SessionTokens.New()) { LivePeer = peer };
            await PersistAsync(session).ConfigureAwait(false);
            return session;
        }

        /// <inheritdoc/>
        public async Task<Session?> ResumeAsync(string reconnectToken, PeerInfo? peer)
        {
            if (string.IsNullOrEmpty(reconnectToken)) return null;
            var db = Db;
            var idValue = await db.StringGetAsync(TokenKey(reconnectToken)).ConfigureAwait(false);
            if (idValue.IsNullOrEmpty) return null;

            var loaded = await LoadAsync(db, idValue!).ConfigureAwait(false);
            if (loaded == null) return null;

            // Rotate the token (single-use) and refresh activity/TTL, re-binding to the new connection.
            await db.KeyDeleteAsync(TokenKey(reconnectToken)).ConfigureAwait(false);
            loaded.ReconnectToken = SessionTokens.New();
            loaded.LastSeenUtc = DateTime.UtcNow;
            loaded.LivePeer = peer;
            await PersistAsync(loaded).ConfigureAwait(false);
            return loaded;
        }

        /// <inheritdoc/>
        public async Task RemoveAsync(Session session)
        {
            var db = Db;
            await db.KeyDeleteAsync(SessKey(session.SessionId)).ConfigureAwait(false);
            await db.KeyDeleteAsync(TokenKey(session.ReconnectToken)).ConfigureAwait(false);
            await db.SetRemoveAsync(AccountKey(session.AccountId), session.SessionId).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyCollection<Session>> SessionsForAccountAsync(string accountId)
        {
            var db = Db;
            var ids = await db.SetMembersAsync(AccountKey(accountId)).ConfigureAwait(false);
            var list = new List<Session>(ids.Length);
            foreach (var id in ids)
            {
                var session = await LoadAsync(db, id!).ConfigureAwait(false);
                if (session != null) list.Add(session);
                else await db.SetRemoveAsync(AccountKey(accountId), id).ConfigureAwait(false);   // expired → drop stale index entry
            }
            return list;
        }

        /// <inheritdoc/>
        public Task SweepAsync() => Task.CompletedTask;   // Redis TTL evicts idle sessions automatically

        private async Task PersistAsync(Session s)
        {
            var db = Db;
            await db.HashSetAsync(SessKey(s.SessionId), new[]
            {
                new HashEntry("acct", s.AccountId),
                new HashEntry("token", s.ReconnectToken),
                new HashEntry("seen", s.LastSeenUtc.Ticks),
            }).ConfigureAwait(false);
            await db.KeyExpireAsync(SessKey(s.SessionId), _ttl).ConfigureAwait(false);
            await db.StringSetAsync(TokenKey(s.ReconnectToken), s.SessionId, _ttl).ConfigureAwait(false);
            await db.SetAddAsync(AccountKey(s.AccountId), s.SessionId).ConfigureAwait(false);
            await db.KeyExpireAsync(AccountKey(s.AccountId), _ttl).ConfigureAwait(false);
        }

        private async Task<Session?> LoadAsync(IDatabase db, string sessionId)
        {
            var entries = await db.HashGetAllAsync(SessKey(sessionId)).ConfigureAwait(false);
            if (entries.Length == 0) return null;
            var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
            return new Session(sessionId, map["acct"]!, map["token"]!)
            {
                LastSeenUtc = new DateTime((long)map["seen"], DateTimeKind.Utc),
            };
        }
    }

    /// <summary>
    /// Redis-backed <see cref="IRoomStore"/>: room join codes are reserved atomically in Redis, so codes are unique
    /// across the whole cluster and matchmaking on any node can create a room others can look up.
    /// <para>Note: a <see cref="Room"/>'s live members are per-node connections and are not stored in Redis — this
    /// store shares room <b>metadata/codes</b>, not membership or broadcast. Actual room traffic stays node-local
    /// (route players of one room to one node, or relay between nodes with <see cref="T:SetNet.Cluster.ClusterNode"/>).</para>
    /// </summary>
    public sealed class RedisRoomStore : IRoomStore
    {
        private static readonly char[] Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
        private readonly IConnectionMultiplexer _mux;
        private readonly string _prefix;

        /// <summary>Creates the store over a shared multiplexer.</summary>
        public RedisRoomStore(IConnectionMultiplexer mux, string prefix = "setnet:")
        {
            _mux = mux ?? throw new ArgumentNullException(nameof(mux));
            _prefix = prefix;
        }

        private IDatabase Db => _mux.GetDatabase();
        private string Key(string code) => $"{_prefix}room:{code}";

        /// <inheritdoc/>
        public async Task<Room> CreateAsync(int maxPlayers)
        {
            var db = Db;
            while (true)
            {
                var code = GenerateCode();
                if (await db.StringSetAsync(Key(code), maxPlayers, when: When.NotExists).ConfigureAwait(false))
                    return new Room(code, maxPlayers);
                // extremely rare collision — retry with a new code
            }
        }

        /// <inheritdoc/>
        public async Task<Room?> GetAsync(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var value = await Db.StringGetAsync(Key(code)).ConfigureAwait(false);
            return value.IsNullOrEmpty ? null : new Room(code, (int)value);
        }

        /// <inheritdoc/>
        public Task RemoveAsync(Room room) => Db.KeyDeleteAsync(Key(room.Code));

        private static string GenerateCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[6];
            for (var i = 0; i < 6; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }
    }
}
