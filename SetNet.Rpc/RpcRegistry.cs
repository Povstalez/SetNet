using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Rpc
{
    /// <summary>
    /// Process-wide registry of in-flight RPC calls, keyed by a process-unique correlation id. Because the id is
    /// unique across the whole process, the (connection-less) client response handler can complete the right
    /// awaiting call without needing to know which client it belongs to.
    /// </summary>
    internal static class RpcRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<RpcEnvelope>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<RpcEnvelope>>();

        /// <summary>Allocates the next process-unique correlation id.</summary>
        public static int NextId() => Interlocked.Increment(ref _counter);

        /// <summary>Registers the completion source awaiting the response for <paramref name="correlationId"/>.</summary>
        public static void Register(int correlationId, TaskCompletionSource<RpcEnvelope> tcs) => _pending[correlationId] = tcs;

        /// <summary>Removes a pending call (on completion, timeout, or cancellation).</summary>
        public static void Remove(int correlationId) => _pending.TryRemove(correlationId, out _);

        /// <summary>Completes the awaiting call for <paramref name="correlationId"/> with its response, if still pending.</summary>
        public static void Complete(int correlationId, RpcEnvelope response)
        {
            if (_pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(response);
        }
    }
}
