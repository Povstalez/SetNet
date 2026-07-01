using System;
using System.Collections.Concurrent;
using SetNet.Core;

namespace SetNet.BanList
{
    /// <summary>A pluggable ban store keyed by a string (IP address, account id, …). Implementations must be thread-safe.</summary>
    public interface IBanStore
    {
        /// <summary>True if the key is currently banned (an expired timed ban returns false).</summary>
        bool IsBanned(string key);

        /// <summary>Bans a key, optionally until a UTC time (null = permanent).</summary>
        void Ban(string key, DateTime? untilUtc = null);

        /// <summary>Lifts a ban.</summary>
        void Unban(string key);
    }

    /// <summary>Default in-process ban store (a dictionary of key → optional expiry). Swap for a Redis/DB store to share bans across nodes/restarts.</summary>
    public sealed class MemoryBanStore : IBanStore
    {
        // value: null = permanent; otherwise the UTC expiry.
        private readonly ConcurrentDictionary<string, DateTime?> _bans = new ConcurrentDictionary<string, DateTime?>();

        /// <inheritdoc/>
        public bool IsBanned(string key)
        {
            if (key == null || !_bans.TryGetValue(key, out var until)) return false;
            if (until == null) return true;
            if (until.Value > DateTime.UtcNow) return true;
            _bans.TryRemove(key, out _);   // expired — clean up
            return false;
        }

        /// <inheritdoc/>
        public void Ban(string key, DateTime? untilUtc = null) { if (key != null) _bans[key] = untilUtc; }

        /// <inheritdoc/>
        public void Unban(string key) { if (key != null) _bans.TryRemove(key, out _); }
    }

    /// <summary>
    /// Server-side ban enforcement, returned by <see cref="BanListExtensions.UseBanList(BaseServer, IBanStore)"/>. Installs an
    /// inbound gate (chained onto any existing <see cref="BaseServer.InboundAuthorizer"/>, so it composes with Auth/RateLimit)
    /// that drops all frames from a banned peer, and tracks live peers so <see cref="Ban"/> immediately kicks anyone matching.
    /// The ban **key** is derived from a peer by a selector — by default the remote IP address.
    /// </summary>
    public sealed class BanList
    {
        private readonly IBanStore _store;
        private readonly Func<BasePeer, string> _keySelector;
        private readonly ConcurrentDictionary<BasePeer, byte> _peers = new ConcurrentDictionary<BasePeer, byte>();

        internal BanList(BaseServer server, IBanStore store, Func<BasePeer, string> keySelector)
        {
            _store = store;
            _keySelector = keySelector;

            var previous = server.InboundAuthorizer;
            server.InboundAuthorizer = (peer, type) => (previous == null || previous(peer, type)) && !_store.IsBanned(_keySelector(peer));

            server.PeerConnected += p => _peers[p] = 0;
            server.PeerDisconnected += p => _peers.TryRemove(p, out _);
        }

        /// <summary>True if the key is banned.</summary>
        public bool IsBanned(string key) => _store.IsBanned(key);

        /// <summary>Bans a key (IP/account) and immediately kicks any connected peer matching it.</summary>
        public void Ban(string key, DateTime? untilUtc = null)
        {
            _store.Ban(key, untilUtc);
            foreach (var peer in _peers.Keys)
                if (string.Equals(_keySelector(peer), key, StringComparison.Ordinal))
                    Kick(peer);
        }

        /// <summary>Lifts a ban.</summary>
        public void Unban(string key) => _store.Unban(key);

        /// <summary>Immediately disconnects a peer.</summary>
        public void Kick(BasePeer peer)
        {
            try { peer.CurrentPeerInfo.Disconnect(); } catch { /* already gone */ }
        }
    }

    /// <summary>Attaches ban enforcement to a server by composition — no base class.</summary>
    public static class BanListExtensions
    {
        /// <summary>Enables ban enforcement keyed by the peer's remote IP address (default store: in-process).</summary>
        public static BanList UseBanList(this BaseServer server, IBanStore? store = null)
            => UseBanList(server, peer => peer.RemoteEndPoint?.Address.ToString() ?? peer.CurrentPeerInfo.Id.ToString("N"), store);

        /// <summary>Enables ban enforcement keyed by a custom selector (e.g. the authenticated account id).</summary>
        public static BanList UseBanList(this BaseServer server, Func<BasePeer, string> keySelector, IBanStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
            return new BanList(server, store ?? new MemoryBanStore(), keySelector);
        }
    }
}
