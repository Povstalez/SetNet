using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Protocol
{
    /// <summary>
    /// Process-wide registry of in-flight unified-protocol requests, keyed by a process-unique correlation id.
    /// Because the id is unique across the whole process, the (connection-less) client envelope handler can
    /// complete the right awaiting call without knowing which client it belongs to — this is what lets every module
    /// share one request/reply mechanism instead of each maintaining its own.
    /// </summary>
    internal static class ProtocolCorrelation
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<ProtocolEnvelope>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<ProtocolEnvelope>>();

        /// <summary>Allocates the next process-unique correlation id (always non-zero).</summary>
        public static int NextId()
        {
            // Start at 1 so 0 can mean "no correlation" (one-way sends/events).
            var id = Interlocked.Increment(ref _counter);
            return id == 0 ? Interlocked.Increment(ref _counter) : id;
        }

        /// <summary>Registers the completion source awaiting the reply for <paramref name="correlationId"/>.</summary>
        public static void Register(int correlationId, TaskCompletionSource<ProtocolEnvelope> tcs)
            => Pending[correlationId] = tcs;

        /// <summary>Removes a pending call (on completion, timeout, or cancellation).</summary>
        public static void Remove(int correlationId) => Pending.TryRemove(correlationId, out _);

        /// <summary>Completes the awaiting call for <paramref name="correlationId"/> with its reply/error, if still pending.</summary>
        public static void Complete(int correlationId, ProtocolEnvelope reply)
        {
            if (Pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(reply);
        }
    }
}
