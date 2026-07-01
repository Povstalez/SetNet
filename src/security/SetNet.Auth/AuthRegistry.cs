using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Auth
{
    /// <summary>
    /// Process-wide registry of in-flight auth handshakes, keyed by a process-unique correlation id — so the
    /// connection-less client response handler can complete the right awaiting handshake (same technique as RPC).
    /// </summary>
    internal static class AuthRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<AuthResponse>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<AuthResponse>>();

        public static int NextId() => Interlocked.Increment(ref _counter);

        public static void Register(int correlationId, TaskCompletionSource<AuthResponse> tcs) => _pending[correlationId] = tcs;

        public static void Remove(int correlationId) => _pending.TryRemove(correlationId, out _);

        public static void Complete(int correlationId, AuthResponse response)
        {
            if (_pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(response);
        }
    }
}
