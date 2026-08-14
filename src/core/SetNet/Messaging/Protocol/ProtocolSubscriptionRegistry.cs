using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SetNet.Protocol
{
    /// <summary>Client-side push-event subscriptions scoped to a <see cref="SetNetRuntime"/>.</summary>
    public sealed class ProtocolSubscriptionRegistry
    {
        private readonly SetNetRuntime _runtime;
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, Action<byte[]>>> _subs
            = new ConcurrentDictionary<int, ConcurrentDictionary<long, Action<byte[]>>>();

        // Subscribers that take the body as a window onto the received frame instead of an array of their own.
        // Kept in a separate table so the array-based API above is untouched: an event feeds both, and the array
        // is only materialised when somebody actually subscribed for one.
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, Action<ReadOnlyMemory<byte>>>> _memSubs
            = new ConcurrentDictionary<int, ConcurrentDictionary<long, Action<ReadOnlyMemory<byte>>>>();

        private long _token;

        /// <summary>Creates a registry backed by <see cref="SetNetRuntime.Default"/>.</summary>
        public ProtocolSubscriptionRegistry() : this(SetNetRuntime.Default) { }

        internal ProtocolSubscriptionRegistry(SetNetRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        internal SetNetRuntime Runtime => _runtime;

        /// <summary>Adds a subscription for a channel/op pair and returns an unsubscribe handle.</summary>
        public IDisposable Add(ushort channel, ushort op, Action<byte[]> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var key = Key(channel, op);
            var bucket = _subs.GetOrAdd(key, _ => new ConcurrentDictionary<long, Action<byte[]>>());
            var token = Interlocked.Increment(ref _token);
            bucket[token] = callback;
            return new Subscription(this, key, token);
        }

        /// <summary>
        /// Adds a subscription that receives the body as a window onto the received frame, and returns an
        /// unsubscribe handle.
        /// </summary>
        /// <remarks>
        /// The window is valid only for the duration of the callback — the frame it points into is reused
        /// afterwards. Decode from it inside the callback; do not store it. Subscribers that need to keep the
        /// bytes should use <see cref="Add"/> instead and get their own array.
        /// </remarks>
        public IDisposable AddMemory(ushort channel, ushort op, Action<ReadOnlyMemory<byte>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var key = Key(channel, op);
            var bucket = _memSubs.GetOrAdd(key, _ => new ConcurrentDictionary<long, Action<ReadOnlyMemory<byte>>>());
            var token = Interlocked.Increment(ref _token);
            bucket[token] = callback;
            return new Subscription(this, key, token, memory: true);
        }

        /// <summary>Delivers an event body to every subscriber for a channel/op pair.</summary>
        public void Dispatch(ushort channel, ushort op, byte[] body)
            => Dispatch(channel, op, (ReadOnlyMemory<byte>)(body ?? Array.Empty<byte>()), body);

        /// <summary>
        /// Delivers an event body held inside a larger buffer, without copying it out first.
        /// </summary>
        /// <remarks>
        /// Array-based subscribers still need an array, so one is produced — but lazily, and only if such a
        /// subscriber exists for this channel/op. When every subscriber took the memory-based path (the typed
        /// <c>On&lt;T&gt;</c> route with a serializer that can decode from memory), the event travels from socket
        /// to handler with no copy of the body at all.
        /// </remarks>
        public void Dispatch(ushort channel, ushort op, ReadOnlyMemory<byte> body)
            => Dispatch(channel, op, body, null);

        private void Dispatch(ushort channel, ushort op, ReadOnlyMemory<byte> body, byte[]? asArray)
        {
            ClientEventDiscovery.EnsureDiscovered(this);
            var key = Key(channel, op);

            if (_memSubs.TryGetValue(key, out var memBucket))
            {
                // Enumerate the dictionary itself, not its Values: ConcurrentDictionary.Values builds a fresh
                // snapshot list on every access, which is one throwaway list per delivered event. The dictionary's
                // own enumerator is a struct and takes no lock, and it tolerates concurrent writes — exactly what a
                // subscription bucket needs while handlers may subscribe or unsubscribe.
                foreach (var kv in memBucket)
                {
                    try { kv.Value(body); } catch { }
                }
            }

            if (!_subs.TryGetValue(key, out var bucket)) return;

            var array = asArray ?? body.ToArray();
            foreach (var kv in bucket)
            {
                try { kv.Value(array); } catch { }
            }
        }

        private static int Key(ushort channel, ushort op) => (channel << 16) | op;

        private void Remove(int key, long token, bool memory)
        {
            if (memory)
            {
                if (_memSubs.TryGetValue(key, out var memBucket)) memBucket.TryRemove(token, out _);
            }
            else if (_subs.TryGetValue(key, out var bucket))
            {
                bucket.TryRemove(token, out _);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly ProtocolSubscriptionRegistry _owner;
            private readonly int _key;
            private readonly long _token;
            private readonly bool _memory;
            private int _disposed;

            public Subscription(ProtocolSubscriptionRegistry owner, int key, long token, bool memory = false)
            {
                _owner = owner;
                _key = key;
                _token = token;
                _memory = memory;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _owner.Remove(_key, _token, _memory);
            }
        }
    }
}
