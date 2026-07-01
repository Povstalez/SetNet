using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Rooms
{
    /// <summary>
    /// Client-side plumbing: pending command replies keyed by a process-unique correlation id, and the set of
    /// <see cref="RoomsClient"/> instances that server-push events are routed to (each filters by its own room).
    /// </summary>
    internal static class RoomRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<RoomReply>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<RoomReply>>();
        private static readonly ConcurrentDictionary<RoomsClient, byte> _clients
            = new ConcurrentDictionary<RoomsClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int correlationId, TaskCompletionSource<RoomReply> tcs) => _pending[correlationId] = tcs;
        public static void Remove(int correlationId) => _pending.TryRemove(correlationId, out _);

        public static void Complete(int correlationId, RoomReply reply)
        {
            if (_pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(reply);
        }

        public static void RegisterClient(RoomsClient client) => _clients[client] = 0;

        public static void DispatchEvent(RoomEvent evt)
        {
            foreach (var client in _clients.Keys)
                client.OnEvent(evt);
        }
    }
}
