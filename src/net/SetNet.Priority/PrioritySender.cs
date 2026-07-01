using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;

namespace SetNet.Priority
{
    /// <summary>
    /// A per-connection outbound queue that sends higher-priority messages first, with an optional per-flush byte budget.
    /// Enqueue messages with a priority during a tick, then <see cref="FlushAsync"/> — the sender drains from the highest
    /// priority down; if a byte budget is given, low-priority messages that don't fit stay queued for the next flush
    /// (natural back-pressure, and the hook for <c>SetNet.Congestion</c>). Payloads are sent already-serialized (raw), so
    /// serialize once and enqueue the bytes.
    /// </summary>
    public sealed class PrioritySender
    {
        private readonly struct Item
        {
            public readonly ushort Type;
            public readonly byte[] Payload;
            public readonly DeliveryMethod Delivery;
            public Item(ushort type, byte[] payload, DeliveryMethod delivery) { Type = type; Payload = payload; Delivery = delivery; }
        }

        private readonly Func<ushort, byte[], DeliveryMethod, Task> _sendRaw;
        private readonly object _gate = new object();
        // Keyed by -priority so the SortedDictionary iterates highest priority first.
        private readonly SortedDictionary<int, Queue<Item>> _buckets = new SortedDictionary<int, Queue<Item>>();
        private int _queuedBytes;

        /// <summary>Wraps a client's raw send path.</summary>
        public PrioritySender(BaseClient client) => _sendRaw = (t, p, d) => client.SendRawAsync(t, p, d);

        /// <summary>Wraps a server peer's raw send path.</summary>
        public PrioritySender(BasePeer peer) => _sendRaw = (t, p, d) => peer.SendRawAsync(t, p, d);

        /// <summary>Total bytes currently queued across all priorities.</summary>
        public int QueuedBytes { get { lock (_gate) return _queuedBytes; } }

        /// <summary>Enqueues an already-serialized message at the given priority (higher = sent sooner).</summary>
        public void Enqueue(ushort type, byte[] payload, int priority, DeliveryMethod delivery = DeliveryMethod.Reliable)
        {
            payload = payload ?? Array.Empty<byte>();
            lock (_gate)
            {
                if (!_buckets.TryGetValue(-priority, out var q)) { q = new Queue<Item>(); _buckets[-priority] = q; }
                q.Enqueue(new Item(type, payload, delivery));
                _queuedBytes += payload.Length;
            }
        }

        /// <summary>
        /// Sends queued messages highest-priority-first. When <paramref name="maxBytes"/> is set, stops once that many
        /// payload bytes have been sent this flush; the remainder stays queued. Returns the number of messages sent.
        /// </summary>
        public async Task<int> FlushAsync(int? maxBytes = null)
        {
            var toSend = new List<Item>();
            lock (_gate)
            {
                var budget = maxBytes ?? int.MaxValue;
                var emptyKeys = new List<int>();
                foreach (var kv in _buckets)
                {
                    var q = kv.Value;
                    while (q.Count > 0)
                    {
                        var peek = q.Peek();
                        if (toSend.Count > 0 && peek.Payload.Length > budget) break;   // out of budget (always send at least one)
                        q.Dequeue();
                        toSend.Add(peek);
                        _queuedBytes -= peek.Payload.Length;
                        budget -= peek.Payload.Length;
                    }
                    if (q.Count == 0) emptyKeys.Add(kv.Key);
                    if (budget <= 0) break;
                }
                foreach (var k in emptyKeys) _buckets.Remove(k);
            }

            foreach (var item in toSend)
                await _sendRaw(item.Type, item.Payload, item.Delivery).ConfigureAwait(false);
            return toSend.Count;
        }
    }
}
