using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace SetNet.Core
{
    /// <summary>
    /// Fixed-window per-IP rate limiter used to throttle new connections/handshakes and blunt connection
    /// floods. Each remote IP gets an allowance of N events per one-second window. Thread-safe.
    /// </summary>
    internal sealed class RateLimiter
    {
        /// <summary>Upper bound on tracked IPs; beyond this, unseen IPs are allowed so the table can't grow without bound under a spoofed-source flood.</summary>
        private const int MaxTrackedIps = 50_000;

        /// <summary>Allowed events per IP per one-second window; values &lt;= 0 disable limiting.</summary>
        private readonly int _maxPerSecond;

        /// <summary>Per-IP sliding window state.</summary>
        private readonly ConcurrentDictionary<IPAddress, Window> _windows = new ConcurrentDictionary<IPAddress, Window>();

        /// <summary>Monotonic timestamp of the last idle-window prune, used to throttle sweeps to at most once per second.</summary>
        private long _lastPruneTicks = MonotonicClock.Timestamp;

        /// <summary>The start timestamp and event count of one IP's current window.</summary>
        private sealed class Window
        {
            /// <summary>Monotonic timestamp at which the current 1-second window started.</summary>
            public long Start;

            /// <summary>Number of events counted in the current window.</summary>
            public int Count;
        }

        /// <summary>Creates a limiter allowing <paramref name="maxPerSecond"/> events per IP per second (0 = unlimited).</summary>
        /// <param name="maxPerSecond">Maximum allowed events per IP per one-second window; 0 or less disables limiting.</param>
        public RateLimiter(int maxPerSecond)
        {
            _maxPerSecond = maxPerSecond;
        }

        /// <summary>Records and tests one event from <paramref name="ip"/>.</summary>
        /// <param name="ip">The remote IP address attempting a new connection/handshake.</param>
        /// <returns><c>true</c> if the event is within the per-second allowance; <c>false</c> to reject it.</returns>
        public bool Allow(IPAddress ip)
        {
            if (_maxPerSecond <= 0) return true;
            PruneIdle();
            if (_windows.Count > MaxTrackedIps && !_windows.ContainsKey(ip))
                return true; // memory guard: stop tracking new IPs under a spoofed-source flood

            var window = _windows.GetOrAdd(ip, _ => new Window { Start = MonotonicClock.Timestamp, Count = 0 });
            lock (window)
            {
                if (MonotonicClock.ElapsedMs(window.Start) >= 1000)
                {
                    window.Start = MonotonicClock.Timestamp;
                    window.Count = 0;
                }

                if (window.Count >= _maxPerSecond)
                    return false;

                window.Count++;
                return true;
            }
        }

        /// <summary>
        /// Periodically (at most once per second) evicts windows whose one-second slot has fully elapsed, so the
        /// tracking table stays bounded by the set of recently-active IPs instead of accumulating one permanent
        /// entry per IP ever seen. Without this the table could drift to <see cref="MaxTrackedIps"/> from ordinary
        /// transient traffic and then silently stop limiting new IPs.
        /// </summary>
        private void PruneIdle()
        {
            var last = Interlocked.Read(ref _lastPruneTicks);
            if (MonotonicClock.ElapsedMs(last) < 1000) return;
            // Claim the prune slot; if another thread won the race, skip (only one sweeper per interval).
            if (Interlocked.CompareExchange(ref _lastPruneTicks, MonotonicClock.Timestamp, last) != last) return;

            foreach (var kv in _windows)
            {
                // A window idle for >= 2s cannot be holding back any current event; drop it. If the IP is
                // active again it is cheaply re-added by GetOrAdd on its next event.
                if (MonotonicClock.ElapsedMs(kv.Value.Start) >= 2000)
                    _windows.TryRemove(kv.Key, out _);
            }
        }
    }
}
