using System.Collections.Concurrent;
using System.Net;

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
    }
}
