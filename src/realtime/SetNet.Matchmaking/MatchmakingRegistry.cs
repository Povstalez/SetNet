using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Matchmaking
{
    /// <summary>
    /// Client-side plumbing: pending command replies keyed by a process-unique correlation id, and the set of
    /// <see cref="MatchmakingClient"/> instances that server-push match events are routed to (each filters by its own
    /// waiting queue and player id).
    /// </summary>
    internal static class MatchmakingRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<MatchReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<MatchReply>>();
        private static readonly ConcurrentDictionary<MatchmakingClient, byte> Clients
            = new ConcurrentDictionary<MatchmakingClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int correlationId, TaskCompletionSource<MatchReply> tcs) => Pending[correlationId] = tcs;
        public static void Remove(int correlationId) => Pending.TryRemove(correlationId, out _);

        public static void Complete(int correlationId, MatchReply reply)
        {
            if (Pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(reply);
        }

        public static void RegisterClient(MatchmakingClient client) => Clients[client] = 0;

        public static void DispatchEvent(MatchEvent evt)
        {
            foreach (var client in Clients.Keys)
                client.OnEvent(evt);
        }
    }
}
