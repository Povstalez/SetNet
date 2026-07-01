using System;

namespace SetNet.RateLimit
{
    /// <summary>
    /// A classic token bucket: refills at a steady rate up to a burst capacity; each admitted item consumes one
    /// token. Cheap and lock-friendly — a single struct-of-doubles guarded by the caller's lock.
    /// </summary>
    internal sealed class TokenBucket
    {
        private readonly double _ratePerSecond;
        private readonly double _capacity;
        private double _tokens;
        private long _lastTicks;
        private readonly object _gate = new object();

        public TokenBucket(double ratePerSecond, double capacity)
        {
            _ratePerSecond = ratePerSecond <= 0 ? 1 : ratePerSecond;
            _capacity = capacity <= 0 ? 1 : capacity;
            _tokens = _capacity;
            _lastTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>Tries to consume one token; returns true if allowed, false if the bucket is empty (rate exceeded).</summary>
        public bool TryConsume()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow.Ticks;
                var elapsedSeconds = (now - _lastTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0)
                {
                    _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _ratePerSecond);
                    _lastTicks = now;
                }

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }
                return false;
            }
        }
    }
}
