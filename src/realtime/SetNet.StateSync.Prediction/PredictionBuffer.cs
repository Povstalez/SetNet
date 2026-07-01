using System;
using System.Collections.Generic;

namespace SetNet.StateSync.Prediction
{
    /// <summary>
    /// Client-side prediction bookkeeping for the owned entity. Record each input you apply locally together with the
    /// sequence number the server echoes back (<c>ClientReplication.LastProcessedInput</c>). When a corrective snapshot
    /// arrives, snap your entity to the server state, then <see cref="Reconcile"/>: it discards inputs the server has
    /// already processed and **replays** the still-unacknowledged ones on top of the authoritative state — so the local
    /// player keeps moving without lag while staying server-authoritative (rewind &amp; replay).
    /// </summary>
    /// <typeparam name="TInput">Your per-tick input type (movement, buttons, …).</typeparam>
    public sealed class PredictionBuffer<TInput>
    {
        private readonly struct Entry { public readonly uint Seq; public readonly TInput Input; public Entry(uint seq, TInput input) { Seq = seq; Input = input; } }

        private readonly object _gate = new object();
        private readonly List<Entry> _pending = new List<Entry>();
        private readonly int _max;

        /// <summary>Creates the buffer, keeping at most <paramref name="maxPending"/> unacknowledged inputs.</summary>
        public PredictionBuffer(int maxPending = 256) => _max = Math.Max(1, maxPending);

        /// <summary>Number of inputs awaiting server acknowledgement.</summary>
        public int PendingCount { get { lock (_gate) return _pending.Count; } }

        /// <summary>Records an input you just applied locally, tagged with the sequence returned by <c>SendInput</c>.</summary>
        public void Record(uint seq, TInput input)
        {
            lock (_gate)
            {
                _pending.Add(new Entry(seq, input));
                if (_pending.Count > _max) _pending.RemoveRange(0, _pending.Count - _max);
            }
        }

        /// <summary>
        /// Drops inputs already processed by the server (seq ≤ <paramref name="lastProcessedInput"/>) and replays the rest,
        /// in order, via <paramref name="apply"/>. Call after snapping your entity to the latest server state.
        /// </summary>
        public void Reconcile(uint lastProcessedInput, Action<TInput> apply)
        {
            List<TInput> replay;
            lock (_gate)
            {
                _pending.RemoveAll(e => Acked(e.Seq, lastProcessedInput));
                replay = new List<TInput>(_pending.Count);
                foreach (var e in _pending) replay.Add(e.Input);
            }
            if (apply != null) foreach (var input in replay) apply(input);
        }

        // Sequence numbers are process-unique and monotonic; a simple <= handles the common case (no wrap in practice).
        private static bool Acked(uint seq, uint lastProcessed) => seq <= lastProcessed;
    }
}
