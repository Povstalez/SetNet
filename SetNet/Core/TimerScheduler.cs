using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Core
{
    /// <summary>
    /// A single shared periodic-callback driver that replaces per-connection <c>Task.Delay</c> loops for
    /// heartbeats and reliability ticks. One background loop multiplexes many registered intervals, so a
    /// server with thousands of connections has one timer loop instead of thousands of timer state machines.
    /// </summary>
    /// <remarks>
    /// Callbacks run on the scheduler loop and must be cheap and non-blocking — do synchronous bookkeeping and
    /// fire-and-forget any async work. Exceptions thrown by a callback are swallowed so one bad callback cannot
    /// stop the loop. The process-wide <see cref="Shared"/> instance lives for the lifetime of the process.
    /// </remarks>
    internal sealed class TimerScheduler : IDisposable
    {
        /// <summary>Process-wide scheduler shared by all connections; never disposed explicitly.</summary>
        public static readonly TimerScheduler Shared = new TimerScheduler();

        /// <summary>One registered periodic callback and its next due time.</summary>
        private sealed class Entry
        {
            /// <summary>Firing period in milliseconds.</summary>
            public int IntervalMs;

            /// <summary>Monotonic timestamp at which the callback should next run.</summary>
            public long DueTimestamp;

            /// <summary>The callback to invoke each period.</summary>
            public Action Callback = null!;
        }

        /// <summary>Registered callbacks keyed by registration id.</summary>
        private readonly ConcurrentDictionary<long, Entry> _entries = new ConcurrentDictionary<long, Entry>();

        /// <summary>Monotonic source of registration ids.</summary>
        private long _nextId;

        /// <summary>Cancels the background loop on dispose.</summary>
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Base granularity of the loop; entries fire at most this coarsely.</summary>
        private const int BaseTickMs = 5;

        /// <summary>Starts the background loop.</summary>
        public TimerScheduler()
        {
            _ = RunLoopAsync();
        }

        /// <summary>
        /// Registers a callback to run every <paramref name="intervalMs"/> milliseconds.
        /// </summary>
        /// <param name="intervalMs">The firing period in milliseconds.</param>
        /// <param name="callback">The (fast, non-blocking) callback to invoke each period.</param>
        /// <param name="initialDelayMs">Delay before the first invocation; defaults to <paramref name="intervalMs"/>.</param>
        /// <returns>A registration id to pass to <see cref="Unschedule"/> when the callback is no longer needed.</returns>
        public long Schedule(int intervalMs, Action callback, int initialDelayMs = -1)
        {
            if (intervalMs < 1) intervalMs = 1;
            var id = Interlocked.Increment(ref _nextId);
            _entries[id] = new Entry
            {
                IntervalMs = intervalMs,
                DueTimestamp = MonotonicClock.DueInMs(initialDelayMs >= 0 ? initialDelayMs : intervalMs),
                Callback = callback
            };
            return id;
        }

        /// <summary>Cancels a previously registered callback. Safe to call multiple times.</summary>
        /// <param name="id">The id returned by <see cref="Schedule"/>.</param>
        public void Unschedule(long id) => _entries.TryRemove(id, out _);

        /// <summary>The background loop: wakes every <see cref="BaseTickMs"/> and fires any due callbacks.</summary>
        private async Task RunLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(BaseTickMs, _cts.Token).ConfigureAwait(false);
                    // Read the clock ONCE per tick and compare every entry against the local, instead of a
                    // Stopwatch.GetTimestamp() syscall per entry — so the scan cost scales as cheap integer
                    // comparisons (O(entries)) rather than O(entries) syscalls at 200 Hz.
                    var now = MonotonicClock.Timestamp;
                    foreach (var kv in _entries)
                    {
                        var entry = kv.Value;
                        if (entry.DueTimestamp > now) continue;

                        // Re-arm from "now" so a slow tick does not cause a catch-up storm.
                        entry.DueTimestamp = MonotonicClock.DueInMs(entry.IntervalMs);
                        try { entry.Callback(); }
                        catch { /* a faulty callback must not kill the shared loop */ }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>Stops the background loop.</summary>
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
