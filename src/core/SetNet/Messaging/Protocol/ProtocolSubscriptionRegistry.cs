using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SetNet.Protocol
{
    /// <summary>Client-side push-event subscriptions scoped to a <see cref="SetNetRuntime"/>.</summary>
    public sealed class ProtocolSubscriptionRegistry
    {
        /// <summary>
        /// One channel/op bucket: a dictionary for bookkeeping plus a snapshot array for delivery.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the snapshot exists.</b> Delivery used to <c>foreach</c> the ConcurrentDictionary directly, on the
        /// stated grounds that its enumerator is a struct. That is true on .NET 9+, which added a public struct
        /// enumerator — but not on the Mono runtime Unity ships, where <c>GetEnumerator()</c> returns an interface
        /// backed by a compiler-generated class. There it is one heap allocation per delivered event, which for a
        /// small event is more than the payload itself.
        /// </para>
        /// <para>
        /// An array read through a plain <c>for</c> allocates nothing on any runtime, and rebuilding it costs
        /// nothing that matters: subscriptions change a handful of times per session, deliveries happen thousands of
        /// times per second.
        /// </para>
        /// <para>
        /// The snapshot is published with <c>Volatile.Write</c>, so a subscription taken out mid-dispatch can
        /// neither be skipped from the array already being walked nor torn — the walker simply finishes with the
        /// list it started on. That is the same tolerance the ConcurrentDictionary gave, kept deliberately.
        /// </para>
        /// </remarks>
        private sealed class Bucket<T> where T : class
        {
            private readonly ConcurrentDictionary<long, T> _map = new ConcurrentDictionary<long, T>();
            private T[] _snapshot = Array.Empty<T>();

            internal T[] Snapshot => Volatile.Read(ref _snapshot);

            internal void Set(long token, T value) { _map[token] = value; Rebuild(); }
            internal void Remove(long token) { if (_map.TryRemove(token, out _)) Rebuild(); }

            private void Rebuild()
            {
                var next = new T[_map.Count];
                int n = 0;
                foreach (var kv in _map)
                {
                    if (n == next.Length) break;   // grew under us — the next Rebuild will catch up
                    next[n++] = kv.Value;
                }
                if (n != next.Length) Array.Resize(ref next, n);
                Volatile.Write(ref _snapshot, next);
            }
        }

        private readonly SetNetRuntime _runtime;
        private readonly ConcurrentDictionary<int, Bucket<Action<byte[]>>> _subs
            = new ConcurrentDictionary<int, Bucket<Action<byte[]>>>();

        // Subscribers that take the body as a window onto the received frame instead of an array of their own.
        // Kept in a separate table so the array-based API above is untouched: an event feeds both, and the array
        // is only materialised when somebody actually subscribed for one.
        private readonly ConcurrentDictionary<int, Bucket<Action<ReadOnlyMemory<byte>>>> _memSubs
            = new ConcurrentDictionary<int, Bucket<Action<ReadOnlyMemory<byte>>>>();

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
            var bucket = _subs.GetOrAdd(key, _ => new Bucket<Action<byte[]>>());
            var token = Interlocked.Increment(ref _token);
            bucket.Set(token, callback);
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
            var bucket = _memSubs.GetOrAdd(key, _ => new Bucket<Action<ReadOnlyMemory<byte>>>());
            var token = Interlocked.Increment(ref _token);
            bucket.Set(token, callback);
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
                // Walk the snapshot array, never the dictionary — see Bucket<T> for why the "struct enumerator"
                // shortcut does not hold on the runtime Unity ships.
                var subs = memBucket.Snapshot;
                for (int i = 0; i < subs.Length; i++)
                {
                    try { subs[i](body); } catch { }
                }
            }

            if (!_subs.TryGetValue(key, out var bucket)) return;

            var array = asArray ?? body.ToArray();
            var arraySubs = bucket.Snapshot;
            for (int i = 0; i < arraySubs.Length; i++)
            {
                try { arraySubs[i](array); } catch { }
            }
        }

        private static int Key(ushort channel, ushort op) => (channel << 16) | op;

        private void Remove(int key, long token, bool memory)
        {
            if (memory)
            {
                if (_memSubs.TryGetValue(key, out var memBucket)) memBucket.Remove(token);
            }
            else if (_subs.TryGetValue(key, out var bucket))
            {
                bucket.Remove(token);
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
