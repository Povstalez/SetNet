using System;
using System.Runtime.CompilerServices;
using SetNet.Core;

namespace SetNet.RateLimit
{
    /// <summary>Per-peer inbound rate-limit settings.</summary>
    public sealed class RateLimitOptions
    {
        /// <summary>Sustained allowed application frames per second, per peer. Default 50.</summary>
        public double PerPeerPerSecond { get; set; } = 50;

        /// <summary>Burst capacity (max frames admitted back-to-back before throttling). Default 100.</summary>
        public double Burst { get; set; } = 100;
    }

    /// <summary>
    /// Per-peer inbound rate limiting, added by composition. <see cref="UseRateLimit"/> installs a token-bucket
    /// gate via the core <c>BaseServer.InboundAuthorizer</c>: application frames from a peer over its budget are
    /// dropped before dispatch. Composes with other gates (e.g. SetNet.Auth) — a frame must pass all of them.
    /// </summary>
    public static class RateLimiter
    {
        /// <summary>Enables per-peer inbound rate limiting on a server.</summary>
        /// <param name="server">The server to protect.</param>
        /// <param name="options">Rate/burst settings (defaults if null).</param>
        public static void UseRateLimit(this BaseServer server, RateLimitOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var opts = options ?? new RateLimitOptions();

            // Weak-keyed per-peer buckets: entries auto-clear when the peer is collected after disconnect.
            var buckets = new ConditionalWeakTable<BasePeer, TokenBucket>();

            var previous = server.InboundAuthorizer;
            server.InboundAuthorizer = (peer, type) =>
            {
                if (previous != null && !previous(peer, type)) return false;   // compose with existing gates
                var bucket = buckets.GetValue(peer, _ => new TokenBucket(opts.PerPeerPerSecond, opts.Burst));
                return bucket.TryConsume();   // false → over budget → the frame is dropped before dispatch
            };
        }
    }
}
