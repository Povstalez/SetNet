using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using SetNet.BanList;
using SetNet.Core;

namespace SetNet.DdosGuard
{
    /// <summary>Tunables for the connection-flood guard.</summary>
    public sealed class DdosGuardOptions
    {
        /// <summary>Max new connections allowed from one IP within <see cref="WindowSeconds"/> before it's auto-banned. Default 10.</summary>
        public int MaxConnectionsPerWindow { get; set; } = 10;

        /// <summary>The sliding window length, in seconds. Default 10.</summary>
        public int WindowSeconds { get; set; } = 10;

        /// <summary>How long (seconds) an offending IP stays auto-banned. Default 300 (5 min).</summary>
        public int BanSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Application-level connection-flood protection, layered on top of <see cref="SetNet.BanList"/>. It counts new
    /// connections per source IP over a sliding window (via the core <see cref="BaseServer.PeerConnected"/> event); an IP
    /// that opens too many too fast is temporarily banned — which drops its traffic and kicks its live peers through the
    /// ban gate. Complements the core per-IP accept rate limiter by acting on established peers and persisting a timed ban.
    /// </summary>
    public static class DdosGuard
    {
        private sealed class Counter { public long WindowStart; public int Count; }

        /// <summary>Enables the flood guard and returns the underlying <see cref="BanList"/> (for manual bans/unbans, shared store).</summary>
        public static SetNet.BanList.BanList UseDdosGuard(this BaseServer server, DdosGuardOptions? options = null, IBanStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var opts = options ?? new DdosGuardOptions();
            var bans = server.UseBanList(store);   // IP-keyed gate + kick
            var counters = new ConcurrentDictionary<string, Counter>();

            server.PeerConnected += peer =>
            {
                var ip = peer.RemoteEndPoint?.Address.ToString();
                if (ip == null) return;

                var c = counters.GetOrAdd(ip, _ => new Counter { WindowStart = Stopwatch.GetTimestamp() });
                int count;
                lock (c)
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = (now - c.WindowStart) / (double)Stopwatch.Frequency;
                    if (elapsed > opts.WindowSeconds) { c.WindowStart = now; c.Count = 0; }
                    c.Count++;
                    count = c.Count;
                }

                if (count > opts.MaxConnectionsPerWindow)
                    bans.Ban(ip, DateTime.UtcNow.AddSeconds(opts.BanSeconds));   // drops traffic + kicks this IP's peers
            };

            return bans;
        }
    }
}
