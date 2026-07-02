using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SetNet.Protocol
{
    /// <summary>
    /// Process-wide registry of client-side push-event subscriptions, keyed by (channel, op). Every module that
    /// used to keep its own static "client registry + DispatchEvent" now routes server push events through here, so
    /// there is one shared subscription mechanism. Each callback receives the raw event body; typed
    /// <c>On&lt;T&gt;</c> overloads wrap a deserializing closure around it.
    /// </summary>
    /// <remarks>
    /// Subscriptions are process-wide (not scoped to a single <c>BaseClient</c>), matching the pre-existing
    /// one-client-per-process assumption of the module event registries. With multiple co-located clients an event
    /// is delivered to every subscriber for its (channel, op); modules that need to disambiguate include an
    /// identifier (e.g. a room code) in the body and filter inside the callback, exactly as before.
    /// </remarks>
    internal static class ProtocolSubscriptions
    {
        // key = (channel << 16) | op  →  (token → callback)
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<long, Action<byte[]>>> Subs
            = new ConcurrentDictionary<int, ConcurrentDictionary<long, Action<byte[]>>>();
        private static long _token;

        private static int Key(ushort channel, ushort op) => (channel << 16) | op;

        /// <summary>Adds a subscription for (channel, op); returns an <see cref="IDisposable"/> that removes it.</summary>
        public static IDisposable Add(ushort channel, ushort op, Action<byte[]> callback)
        {
            var key = Key(channel, op);
            var bucket = Subs.GetOrAdd(key, _ => new ConcurrentDictionary<long, Action<byte[]>>());
            var token = Interlocked.Increment(ref _token);
            bucket[token] = callback;
            return new Subscription(key, token);
        }

        /// <summary>Delivers an event body to every subscriber for (channel, op). Faulty callbacks are isolated.</summary>
        public static void Dispatch(ushort channel, ushort op, byte[] body)
        {
            if (!Subs.TryGetValue(Key(channel, op), out var bucket)) return;
            foreach (var cb in bucket.Values)
            {
                try { cb(body); } catch { /* isolate a faulty subscriber from the receive path */ }
            }
        }

        private static void Remove(int key, long token)
        {
            if (Subs.TryGetValue(key, out var bucket))
                bucket.TryRemove(token, out _);
        }

        /// <summary>Handle returned to a subscriber; disposing it unsubscribes.</summary>
        private sealed class Subscription : IDisposable
        {
            private readonly int _key;
            private readonly long _token;
            private int _disposed;

            public Subscription(int key, long token) { _key = key; _token = token; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Remove(_key, _token);
            }
        }
    }
}
