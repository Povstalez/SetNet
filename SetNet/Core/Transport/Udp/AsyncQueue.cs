using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Minimal single-consumer async FIFO. Used to hand decoded frames from the demux/receive loop to a
    /// connection's <c>ReceiveAsync</c>. <see cref="DequeueAsync"/> returns <c>(false, default)</c> at
    /// end-of-stream, so the item type may be a value type (no boxing) as well as a reference type.
    /// </summary>
    /// <typeparam name="T">The item type being queued (e.g. a transport message struct).</typeparam>
    /// <remarks>
    /// Producers (the receive/demux loop) may call <see cref="Enqueue"/> from any thread; the
    /// underlying <see cref="ConcurrentQueue{T}"/> makes that safe. <see cref="DequeueAsync"/> is
    /// intended for a <em>single</em> consumer — concurrent dequeuers are not coordinated and may
    /// observe lost wake-ups. The <see cref="SemaphoreSlim"/> count tracks the number of pending
    /// items plus one extra release on completion, so awaiters are never starved.
    /// </remarks>
    internal sealed class AsyncQueue<T>
    {
        /// <summary>Backing store for queued items; concurrent so producers and the consumer can touch it without locking.</summary>
        private readonly ConcurrentQueue<T> _items = new ConcurrentQueue<T>();

        /// <summary>
        /// Counts available "wake" tokens — one per enqueued item and one extra on completion —
        /// so <see cref="DequeueAsync"/> can asynchronously block until there is something to return.
        /// </summary>
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

        /// <summary>Maximum number of pending items before <see cref="TryEnqueue"/> rejects new ones; 0 = unbounded.</summary>
        private readonly int _capacity;

        /// <summary>Approximate current pending-item count, maintained for the <see cref="_capacity"/> check.</summary>
        private int _count;

        /// <summary>Set once <see cref="Complete"/> has been called; tells waiting consumers to return the EOF sentinel.</summary>
        private volatile bool _completed;

        /// <summary>Creates a queue with an optional capacity bound.</summary>
        /// <param name="capacity">Maximum pending items before <see cref="TryEnqueue"/> drops; 0 (default) is unbounded.</param>
        public AsyncQueue(int capacity = 0)
        {
            _capacity = capacity;
        }

        /// <summary>
        /// Adds an item to the tail of the queue and wakes one waiting consumer, ignoring the capacity bound.
        /// Use for control queues that must never drop (e.g. the accept queue). Prefer <see cref="TryEnqueue"/>
        /// for data queues that should shed load instead of growing without bound.
        /// </summary>
        /// <param name="item">The item to enqueue; ownership is handed to the consumer that dequeues it.</param>
        public void Enqueue(T item)
        {
            _items.Enqueue(item);
            Interlocked.Increment(ref _count);
            _signal.Release();
        }

        /// <summary>
        /// Adds an item only if the queue is below its capacity bound, returning <c>false</c> (item dropped) when
        /// full. Lets the receive path shed load — dropping best-effort UDP, or failing a reliable peer — instead
        /// of growing the queue without limit under a fast sender or slow consumer (OOM protection).
        /// </summary>
        /// <param name="item">The item to enqueue.</param>
        /// <returns><c>true</c> if enqueued; <c>false</c> if the queue was at capacity and the item was dropped.</returns>
        public bool TryEnqueue(T item)
        {
            if (_capacity > 0 && Volatile.Read(ref _count) >= _capacity)
                return false;
            _items.Enqueue(item);
            Interlocked.Increment(ref _count);
            _signal.Release();
            return true;
        }

        /// <summary>
        /// Marks the queue as finished so that, once drained, <see cref="DequeueAsync"/> returns
        /// <c>null</c> (EOF) instead of blocking forever. Exists to signal a clean shutdown of the
        /// receive path to its single consumer. Idempotent — repeated calls are no-ops.
        /// </summary>
        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            // Release once so a consumer currently parked in DequeueAsync wakes and observes _completed.
            _signal.Release();
        }

        /// <summary>
        /// Asynchronously removes and returns the next item, waiting if the queue is currently empty.
        /// This is the consumer half of the FIFO that drives a connection's receive loop.
        /// </summary>
        /// <param name="ct">Token used to abort the wait; cancellation surfaces as an <see cref="System.OperationCanceledException"/>.</param>
        /// <returns>
        /// <c>(true, item)</c> with the next dequeued item, or <c>(false, default)</c> once the queue has been
        /// completed via <see cref="Complete"/> and fully drained (end-of-stream).
        /// </returns>
        /// <exception cref="System.OperationCanceledException">Thrown if <paramref name="ct"/> is cancelled while waiting.</exception>
        public async Task<(bool ok, T item)> DequeueAsync(CancellationToken ct = default)
        {
            while (true)
            {
                // Block until either an item was enqueued or completion was signalled.
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                if (_items.TryDequeue(out var item))
                {
                    Interlocked.Decrement(ref _count);
                    return (true, item);
                }
                // Woken with nothing to take and the queue is done: report EOF.
                if (_completed)
                    return (false, default!);
            }
        }
    }
}
