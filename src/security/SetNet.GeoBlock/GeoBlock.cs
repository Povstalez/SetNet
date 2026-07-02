using System;
using System.Collections.Generic;
using System.Net;
using SetNet.Core;

namespace SetNet.GeoBlock
{
    /// <summary>
    /// Resolves an ISO 3166-1 alpha-2 country code (e.g. "UA", "US") from an IP address. Pluggable so you can back it
    /// with MaxMind GeoLite2, IP2Location, an HTTP GeoIP API, or a static test map — the package ships no database.
    /// Return <c>null</c> when the country is unknown.
    /// </summary>
    public interface IGeoResolver
    {
        /// <summary>The country code for an address, or null if unknown.</summary>
        string? CountryOf(IPAddress address);
    }

    /// <summary>Whether the country set is a blocklist (reject those) or an allowlist (accept only those).</summary>
    public enum GeoPolicy
    {
        /// <summary>Reject peers whose country is in <see cref="GeoBlockOptions.Countries"/>.</summary>
        Blocklist,
        /// <summary>Reject peers whose country is NOT in <see cref="GeoBlockOptions.Countries"/>.</summary>
        Allowlist,
    }

    /// <summary>Options for geo-based connection filtering.</summary>
    public sealed class GeoBlockOptions
    {
        /// <summary>Blocklist or allowlist semantics for <see cref="Countries"/>.</summary>
        public GeoPolicy Policy { get; set; } = GeoPolicy.Blocklist;

        /// <summary>The country codes (ISO alpha-2, case-insensitive) the policy applies to.</summary>
        public IReadOnlyCollection<string> Countries { get; set; } = Array.Empty<string>();

        /// <summary>What to do when the country can't be resolved (no endpoint / unknown IP). Default: allow.</summary>
        public bool BlockUnknown { get; set; } = false;
    }

    /// <summary>
    /// Rejects connections by geographic origin. On connect it resolves the peer's country from its remote IP and kicks
    /// it immediately if the policy disallows it — so blocked peers never get to send application frames. Attach with
    /// <see cref="GeoBlockExtensions.UseGeoBlock"/>. Requires a transport that exposes a remote endpoint (TCP/UDP/WebSockets).
    /// </summary>
    public sealed class GeoBlock
    {
        private readonly IGeoResolver _resolver;
        private readonly GeoPolicy _policy;
        private readonly HashSet<string> _countries;
        private readonly bool _blockUnknown;

        /// <summary>Raised when a peer is blocked (args: the peer, its resolved country or null).</summary>
        public event Action<BasePeer, string?>? Blocked;

        internal GeoBlock(BaseServer server, IGeoResolver resolver, GeoBlockOptions options)
        {
            _resolver = resolver;
            _policy = options.Policy;
            _countries = new HashSet<string>(options.Countries, StringComparer.OrdinalIgnoreCase);
            _blockUnknown = options.BlockUnknown;
            server.PeerConnected += OnPeerConnected;
        }

        private void OnPeerConnected(BasePeer peer)
        {
            var ip = peer.RemoteEndPoint?.Address;
            var country = ip != null ? _resolver.CountryOf(ip) : null;
            if (!IsBlocked(country)) return;

            Blocked?.Invoke(peer, country);
            try { peer.CurrentPeerInfo.Disconnect(); } catch { /* already gone */ }
        }

        private bool IsBlocked(string? country)
        {
            if (country == null) return _blockUnknown;
            var listed = _countries.Contains(country);
            return _policy == GeoPolicy.Allowlist ? !listed : listed;
        }
    }

    /// <summary>Attaches geo-blocking to a server by composition — no base class.</summary>
    public static class GeoBlockExtensions
    {
        /// <summary>
        /// Enables geo-based connection filtering. Supply an <see cref="IGeoResolver"/> (e.g. a MaxMind GeoLite2 wrapper)
        /// and a blocklist/allowlist policy; blocked peers are kicked on connect.
        /// </summary>
        public static GeoBlock UseGeoBlock(this BaseServer server, IGeoResolver resolver, GeoBlockOptions options)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new GeoBlock(server, resolver, options);
        }
    }
}
