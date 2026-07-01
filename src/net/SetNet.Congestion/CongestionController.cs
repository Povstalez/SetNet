using System;

namespace SetNet.Congestion
{
    /// <summary>
    /// A simple AIMD (additive-increase / multiplicative-decrease) congestion controller — the same family TCP uses. Feed
    /// it your own delivery/loss signals (from reliable-UDP acks, app-level acks, or a StateSync snapshot ack gap) and it
    /// maintains a target **send rate in bytes per second**: each acknowledged round nudges the rate up a little, each
    /// detected loss cuts it back sharply. Ask it for a byte budget per tick and hand that to a
    /// <c>SetNet.Priority.PrioritySender</c> so the connection sheds low-priority traffic under congestion instead of
    /// bloating queues.
    /// </summary>
    public sealed class CongestionController
    {
        private readonly double _minRate;
        private readonly double _maxRate;
        private readonly double _increaseBytesPerAck;
        private readonly double _decreaseFactor;
        private readonly object _gate = new object();
        private double _rate;

        /// <summary>Creates a controller with rate bounds and AIMD steps (bytes/sec).</summary>
        /// <param name="startRate">Initial target rate (bytes/sec). Default 64 KB/s.</param>
        /// <param name="minRate">Floor the rate never drops below. Default 8 KB/s.</param>
        /// <param name="maxRate">Ceiling the rate never rises above. Default 8 MB/s.</param>
        /// <param name="increaseBytesPerAck">Additive increase applied per <see cref="OnDelivered"/>. Default 2 KB.</param>
        /// <param name="decreaseFactor">Multiplier applied per <see cref="OnLoss"/> (0..1). Default 0.7.</param>
        public CongestionController(double startRate = 64_000, double minRate = 8_000, double maxRate = 8_000_000,
            double increaseBytesPerAck = 2_000, double decreaseFactor = 0.7)
        {
            _minRate = minRate;
            _maxRate = maxRate;
            _increaseBytesPerAck = increaseBytesPerAck;
            _decreaseFactor = Math.Min(0.99, Math.Max(0.1, decreaseFactor));
            _rate = Clamp(startRate);
        }

        /// <summary>The current target send rate, in bytes per second.</summary>
        public double RateBytesPerSecond { get { lock (_gate) return _rate; } }

        /// <summary>Signal a successful delivery/ack — nudges the rate up (additive increase).</summary>
        public void OnDelivered() { lock (_gate) _rate = Clamp(_rate + _increaseBytesPerAck); }

        /// <summary>Signal a detected loss/timeout — cuts the rate back (multiplicative decrease).</summary>
        public void OnLoss() { lock (_gate) _rate = Clamp(_rate * _decreaseFactor); }

        /// <summary>The number of payload bytes allowed in an interval of the given length — feed to a priority sender's flush.</summary>
        public int BudgetForInterval(double seconds)
        {
            lock (_gate) return (int)Math.Max(0, Math.Min(int.MaxValue, _rate * seconds));
        }

        private double Clamp(double v) => v < _minRate ? _minRate : (v > _maxRate ? _maxRate : v);
    }
}
