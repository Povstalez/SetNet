using System.Diagnostics;

namespace SetNet.Core
{
    /// <summary>
    /// Monotonic time source for liveness/timeout checks. Cheaper than <c>DateTime.UtcNow</c> and
    /// immune to wall-clock adjustments. Timestamps are opaque <see cref="Stopwatch"/> ticks; use
    /// <see cref="ElapsedMs"/> to measure elapsed milliseconds since a stored timestamp.
    /// </summary>
    internal static class MonotonicClock
    {
        /// <summary>
        /// Captures the current monotonic timestamp in opaque <see cref="Stopwatch"/> ticks. Store this value
        /// (e.g. at the moment a Ping is sent or a message arrives) and later pass it to <see cref="ElapsedMs"/>
        /// to measure how much time has passed. The value is meaningful only relative to other timestamps from
        /// this clock — it does not map to wall-clock time.
        /// </summary>
        public static long Timestamp => Stopwatch.GetTimestamp();

        /// <summary>
        /// Computes the milliseconds elapsed between a previously captured <see cref="Timestamp"/> and now.
        /// Used for liveness and timeout checks (heartbeat windows, connect timeouts) without the cost or
        /// wall-clock sensitivity of <c>DateTime.UtcNow</c>.
        /// </summary>
        /// <param name="since">A timestamp previously obtained from <see cref="Timestamp"/>.</param>
        /// <returns>
        /// The number of whole milliseconds elapsed since <paramref name="since"/>. The result is monotonically
        /// non-decreasing for a fixed <paramref name="since"/> and unaffected by system clock adjustments.
        /// </returns>
        public static long ElapsedMs(long since)
            => (Stopwatch.GetTimestamp() - since) * 1000L / Stopwatch.Frequency;

        /// <summary>Returns a future timestamp <paramref name="ms"/> milliseconds from now (for due-time scheduling).</summary>
        /// <param name="ms">Delay in milliseconds from the current instant.</param>
        /// <returns>A monotonic timestamp that <see cref="IsDue"/> will report as reached after the delay.</returns>
        public static long DueInMs(int ms)
            => Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000L;

        /// <summary>Returns true once the current time has reached or passed <paramref name="dueTimestamp"/>.</summary>
        /// <param name="dueTimestamp">A timestamp produced by <see cref="DueInMs"/>.</param>
        /// <returns><c>true</c> if the due time has been reached.</returns>
        public static bool IsDue(long dueTimestamp)
            => Stopwatch.GetTimestamp() >= dueTimestamp;
    }
}
