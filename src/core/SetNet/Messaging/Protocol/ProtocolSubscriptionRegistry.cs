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

        /// <summary>Delivers an event body to every subscriber for a channel/op pair.</summary>
        public void Dispatch(ushort channel, ushort op, byte[] body)
        {
            ClientEventDiscovery.EnsureDiscovered(this);
            if (!_subs.TryGetValue(Key(channel, op), out var bucket)) return;
            foreach (var cb in bucket.Values)
            {
                try { cb(body); } catch { }
            }
        }

        private static int Key(ushort channel, ushort op) => (channel << 16) | op;

        private void Remove(int key, long token)
        {
            if (_subs.TryGetValue(key, out var bucket))
                bucket.TryRemove(token, out _);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly ProtocolSubscriptionRegistry _owner;
            private readonly int _key;
            private readonly long _token;
            private int _disposed;

            public Subscription(ProtocolSubscriptionRegistry owner, int key, long token)
            {
                _owner = owner;
                _key = key;
                _token = token;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _owner.Remove(_key, _token);
            }
        }
    }
}
