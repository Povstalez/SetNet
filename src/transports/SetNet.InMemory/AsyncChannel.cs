using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.InMemory
{
    /// <summary>
    /// A minimal single-reader, multi-writer asynchronous queue used to model an in-memory "wire": writers
    /// enqueue items, a single reader awaits them one at a time, and <see cref="Complete"/> signals end-of-stream
    /// so a pending/next read returns <c>(false, default)</c> once the queue has drained. Works with both value
    /// and reference item types (the in-memory transport uses it for framed messages and for accepted connections).
    /// </summary>
    /// <typeparam name="T">The item type carried by the channel.</typeparam>
    internal sealed class AsyncChannel<T>
    {
        private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private volatile bool _completed;

        /// <summary>Enqueues an item and wakes a waiting reader. Silently ignored once the channel is completed.</summary>
        public void Write(T item)
        {
            if (_completed) return;
            _queue.Enqueue(item);
            try { _signal.Release(); } catch (SemaphoreFullException) { /* saturated; a reader will drain */ }
        }

        /// <summary>Marks the channel complete; after the backlog drains, reads return <c>(false, default)</c>.</summary>
        public void Complete()
        {
            _completed = true;
            try { _signal.Release(); } catch (SemaphoreFullException) { /* a reader is already scheduled */ }
        }

        /// <summary>
        /// Awaits the next item. Returns <c>(true, item)</c> while items remain, or <c>(false, default)</c> once the
        /// channel is completed and drained (the end-of-stream signal the transport surfaces as a graceful close).
        /// </summary>
        public async Task<(bool ok, T item)> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_queue.TryDequeue(out var item)) return (true, item);
                if (_completed) return (false, default!);
                try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
                catch (System.ObjectDisposedException) { return (false, default!); }
            }
        }

        /// <summary>Releases the underlying signal.</summary>
        public void Dispose() { try { _signal.Dispose(); } catch { /* ignore */ } }
    }
}
