using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Optional reliability layer over UDP: sequence numbers, cumulative+bitfield ACKs,
    /// retransmission, a send window for back-pressure, and (optionally) ordered delivery.
    /// Sits between the datagram socket and the application; bypassed entirely when
    /// <see cref="Configuration.UdpReliabilityEnabled"/> is false (the channel is simply not created).
    /// </summary>
    internal sealed class ReliabilityChannel : IDisposable
    {
        /// <summary>Configuration supplying window size, ACK timeout, retransmit cap, ordering toggle, and datagram size limit.</summary>
        private readonly Configuration _config;

        /// <summary>The channel id this instance owns; stamped into outbound reliable/ack datagrams.</summary>
        private readonly byte _channel;

        /// <summary>Egress callback that writes a framed datagram (buffer, length) to the underlying socket.</summary>
        private readonly Func<byte[], int, Task> _sendRaw;

        /// <summary>Sink for decoded, de-duplicated (and optionally reordered) application messages handed to the consumer.</summary>
        private readonly AsyncQueue<TransportMessage> _inbound;

        /// <summary>Optional callback invoked when a packet exhausts its retransmit budget, signalling the connection should be torn down.</summary>
        private readonly Action? _onFailure;

        /// <summary>Guards all sequence/ACK/window state, which is mutated from both send calls and the background tick loop.</summary>
        private readonly object _lock = new object();

        /// <summary>Registration id of this channel's periodic retransmit/ACK tick on the shared scheduler.</summary>
        private readonly long _tickId;

        /// <summary>0/1 guard so overlapping scheduler ticks don't run the retransmit pass concurrently.</summary>
        private int _ticking;

        /// <summary>Set once disposed; short-circuits sends, inbound processing, and the tick.</summary>
        private volatile bool _disposed;

        /// <summary>Flags that at least one ACK is owed; the tick loop coalesces these into a single cumulative ACK per interval.</summary>
        private volatile bool _ackPending; // coalesced ACK flushed on the tick, not per-packet

        // Outbound state
        /// <summary>The next sequence number to assign to an outbound reliable packet; wraps around the 16-bit space.</summary>
        private ushort _nextSeq;

        /// <summary>Outbound packets sent but not yet acknowledged, keyed by sequence number, used for retransmission.</summary>
        private readonly Dictionary<ushort, OutboundPacket> _unacked = new Dictionary<ushort, OutboundPacket>();

        /// <summary>Counting semaphore enforcing the send window: each in-flight unacked packet holds one slot for back-pressure.</summary>
        private readonly SemaphoreSlim _windowSlots;

        /// <summary>Reused scratch list of sequence numbers due for retransmit, refilled each tick to avoid a per-tick allocation. Guarded by <see cref="_lock"/>.</summary>
        private readonly List<ushort> _dueScratch = new List<ushort>();

        // Inbound state
        /// <summary>Ordered-delivery cursor: the next sequence number expected to be delivered to the consumer.</summary>
        private ushort _expectedSeq;                                  // ordered delivery cursor

        /// <summary>Buffer of out-of-order reliable messages held until the gap before them is filled (ordered mode only).</summary>
        private readonly Dictionary<ushort, TransportMessage> _reorder = new Dictionary<ushort, TransportMessage>();

        /// <summary>Whether any reliable datagram has been received yet, distinguishing a true zero-sequence from the initial empty state.</summary>
        private bool _anyReceived;

        /// <summary>The highest sequence number received so far; the anchor for the cumulative+bitfield ACK.</summary>
        private ushort _highestReceivedSeq;

        /// <summary>Bitfield of the 64 sequence numbers immediately preceding <see cref="_highestReceivedSeq"/> that have also been received.</summary>
        private ulong _ackBitfield;

        /// <summary>One outstanding outbound reliable packet: its framed bytes plus the timing/retry bookkeeping for retransmission.</summary>
        private struct OutboundPacket
        {
            /// <summary>The fully framed reliable datagram bytes, retained verbatim so it can be resent unchanged.</summary>
            public byte[] Datagram;

            /// <summary>Monotonic timestamp of the most recent send, compared against the ACK timeout to decide when to retransmit.</summary>
            public long LastSentTicks;

            /// <summary>How many times this packet has been retransmitted, capped by <see cref="Configuration.UdpReliableMaxRetransmits"/>.</summary>
            public int Retransmits;
        }

        /// <summary>
        /// Creates the reliability channel and immediately starts its background retransmit/ACK tick loop. The send
        /// window is sized from configuration so the number of in-flight unacked packets is bounded for back-pressure.
        /// </summary>
        /// <param name="config">Reliability tuning: window size, ACK timeout, retransmit cap, ordering, datagram limit.</param>
        /// <param name="channel">The channel id this instance owns; stamped into every reliable/ack datagram it builds.</param>
        /// <param name="sendRaw">Egress callback writing a framed datagram (buffer, byte count) to the socket.</param>
        /// <param name="inbound">Queue into which decoded, de-duplicated, (optionally) ordered messages are pushed for the consumer.</param>
        /// <param name="onFailure">Optional callback fired when a packet exceeds its retransmit budget, indicating the link should be considered dead.</param>
        public ReliabilityChannel(Configuration config, byte channel, Func<byte[], int, Task> sendRaw, AsyncQueue<TransportMessage> inbound, Action? onFailure = null)
        {
            _config = config;
            _channel = channel;
            _sendRaw = sendRaw;
            _inbound = inbound;
            _onFailure = onFailure;
            _windowSlots = new SemaphoreSlim(config.UdpReliableWindowSize, config.UdpReliableWindowSize);
            _tickId = TimerScheduler.Shared.Schedule(Math.Max(10, config.UdpReliableAckTimeoutMs / 2), OnTick);
        }

        /// <summary>
        /// Compares two sequence numbers using wrap-around (serial-number) arithmetic, so the ordering stays
        /// correct as the 16-bit sequence space rolls over rather than treating wrap as a huge backward jump.
        /// </summary>
        /// <param name="a">The first sequence number.</param>
        /// <param name="b">The second sequence number.</param>
        /// <returns>A negative value if <paramref name="a"/> precedes <paramref name="b"/>, zero if equal, positive if it follows.</returns>
        private static int Compare(ushort a, ushort b) => (short)(a - b); // <0 if a precedes b

        // ── Outbound ──────────────────────────────────────────────────────────
        /// <summary>
        /// Sends an application message reliably: acquires a send-window slot (blocking for back-pressure when the
        /// window is full), assigns the next sequence number, frames and records the packet for retransmission, then
        /// writes it once. Subsequent resends and ACK processing are handled by the tick loop and ACK callbacks.
        /// </summary>
        /// <param name="type">The application-defined message type identifier embedded in the datagram header.</param>
        /// <param name="payload">The serialized message body to deliver.</param>
        /// <param name="ct">Token to cancel while waiting for a free window slot.</param>
        /// <returns>A task that completes once the packet has been recorded and its first transmission written to the socket.</returns>
        /// <exception cref="InvalidOperationException">The channel has been disposed/closed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The framed reliable datagram would exceed <see cref="Configuration.UdpMaxDatagramPayload"/>; the reserved window slot is released before throwing.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting for a window slot.</exception>
        public async Task SendAsync(ushort type, byte[] payload, CancellationToken ct = default)
        {
            if (_disposed) throw new InvalidOperationException("Reliable UDP channel is closed.");
            await _windowSlots.WaitAsync(ct).ConfigureAwait(false);

            byte[] datagram;
            lock (_lock)
            {
                // PEEK the next sequence — do NOT consume it yet. An oversized datagram is rejected below; if we
                // had already incremented _nextSeq it would leave a permanent hole in the sequence space that the
                // receiver's ordered cursor blocks on forever (the seq is never sent, so never retransmitted).
                var seq = _nextSeq;
                datagram = UdpDatagram.BuildReliable(_channel, seq, type, payload);
                if (datagram.Length > _config.UdpMaxDatagramPayload)
                {
                    _windowSlots.Release();
                    throw new ArgumentOutOfRangeException(nameof(payload),
                        $"Reliable UDP datagram ({datagram.Length}B) exceeds UdpMaxDatagramPayload ({_config.UdpMaxDatagramPayload}B).");
                }
                _nextSeq++; // commit the sequence number now that the packet is valid and recorded for delivery/retransmit
                _unacked[seq] = new OutboundPacket
                {
                    Datagram = datagram,
                    LastSentTicks = MonotonicClock.Timestamp,
                    Retransmits = 0
                };
            }

            await _sendRaw(datagram, datagram.Length).ConfigureAwait(false);
        }

        /// <summary>
        /// Processes an inbound ACK datagram from the peer, clearing the acknowledged packets from the unacked set so
        /// they stop being retransmitted. Acks both the cumulative sequence and each older sequence flagged in the
        /// accompanying 64-bit selective bitfield, recovering from lost individual ACKs.
        /// </summary>
        /// <param name="dg">The raw ACK datagram, carrying a cumulative ack sequence plus a bitfield of prior sequences.</param>
        public void OnAck(byte[] dg)
        {
            if (_disposed) return;
            if (!UdpDatagram.TryParseAck(dg, out _, out var ackSeq, out var bitfield)) return;
            _config.Metrics.IncrementReliableAcksReceived();

            lock (_lock)
            {
                AckSeq(ackSeq);
                for (int i = 0; i < 64; i++)
                    if ((bitfield & (1UL << i)) != 0)
                        AckSeq((ushort)(ackSeq - (i + 1)));
            }
        }

        /// <summary>
        /// Marks a single sequence number acknowledged: removes it from the unacked set and frees its send-window
        /// slot so a sender blocked on a full window can proceed.
        /// </summary>
        /// <param name="seq">The sequence number being acknowledged.</param>
        /// <remarks>Must be called under <see cref="_lock"/>. A duplicate ACK for an already-removed sequence is harmless; the <see cref="SemaphoreFullException"/> guard tolerates over-release races.</remarks>
        private void AckSeq(ushort seq)
        {
            if (_unacked.Remove(seq))
            {
                try { _windowSlots.Release(); } catch (SemaphoreFullException) { }
            }
        }

        // ── Inbound ───────────────────────────────────────────────────────────
        /// <summary>
        /// Processes an inbound reliable data datagram: records it for ACK accounting, then delivers it to the
        /// consumer according to the ordering mode. In ordered mode it delivers in-sequence, buffering early
        /// arrivals and flushing them once the gap fills; in unordered mode it delivers any not-yet-seen packet
        /// immediately and drops duplicates. Always marks an ACK pending for the tick loop to flush.
        /// </summary>
        /// <param name="dg">The raw reliable datagram carrying a sequence number, message type, and payload.</param>
        /// <remarks>Delivery to the inbound queue happens outside the lock to avoid holding it across the enqueue; the reorder buffer is bounded to twice the window size to cap memory under sustained loss.</remarks>
        public void OnReliableDatagram(byte[] dg)
        {
            if (_disposed) return;
            if (!UdpDatagram.TryParseReliable(dg, out _, out var seq, out var type, out var payload)) return;

            List<TransportMessage>? toDeliver = null;

            lock (_lock)
            {
                if (_config.UdpOrderedReliable)
                {
                    int cmp = Compare(seq, _expectedSeq);
                    if (cmp == 0)
                    {
                        RecordReceivedForAck(seq); // delivered in order — safe to ACK
                        toDeliver = new List<TransportMessage> { new TransportMessage(type, payload) };
                        _expectedSeq++;
                        while (_reorder.TryGetValue(_expectedSeq, out var buffered))
                        {
                            toDeliver.Add(buffered);
                            _reorder.Remove(_expectedSeq);
                            _expectedSeq++;
                        }
                    }
                    else if (cmp > 0)
                    {
                        // Only ACK a future packet once we have actually retained it. If the reorder buffer is full
                        // we must NOT ACK it — otherwise the sender clears it from _unacked and stops retransmitting,
                        // leaving a permanent gap that wedges the ordered stream forever. Dropping without an ACK
                        // makes the sender retransmit later (natural back-pressure) once buffer space frees.
                        if (_reorder.ContainsKey(seq))
                        {
                            RecordReceivedForAck(seq); // already buffered (duplicate) — we have it, so ACK
                        }
                        else if (_reorder.Count < _config.UdpReliableWindowSize * 2)
                        {
                            _reorder[seq] = new TransportMessage(type, payload);
                            RecordReceivedForAck(seq);
                        }
                        // else: reorder full -> drop WITHOUT acking, so the sender keeps retransmitting it.
                    }
                    else
                    {
                        RecordReceivedForAck(seq); // cmp < 0: already delivered — ACK so the sender stops resending
                    }
                }
                else
                {
                    if (RecordReceivedForAck(seq)) // unordered: deliver any genuinely-new packet, drop duplicates
                        toDeliver = new List<TransportMessage> { new TransportMessage(type, payload) };
                }
            }

            if (toDeliver != null)
                foreach (var m in toDeliver)
                    if (!_inbound.TryEnqueue(m))
                    {
                        // Reliable inbound overflow: silently dropping would leave a permanent gap in the ordered
                        // stream, so fail the connection instead (the consumer is hopelessly behind).
                        _config.Metrics.IncrementInboundDropped();
                        _onFailure?.Invoke();
                        break;
                    }

            // Coalesce: mark an ACK pending; the tick loop flushes one cumulative ACK per interval
            // instead of emitting one ACK datagram per received packet.
            _ackPending = true;
        }

        /// <summary>
        /// Updates the cumulative-ack anchor and selective-ack bitfield to reflect that <paramref name="seq"/> was
        /// received, advancing the high-water mark for newer packets and setting the appropriate bit for slightly
        /// older ones. Drives both the ACK we send back and duplicate detection in unordered mode.
        /// </summary>
        /// <param name="seq">The sequence number of the just-received reliable datagram.</param>
        /// <returns><c>true</c> if this sequence had not been recorded before (a genuinely new packet); <c>false</c> if it is a duplicate or too old to track.</returns>
        /// <remarks>Must be called under <see cref="_lock"/>. Packets more than 64 sequences behind the high-water mark fall outside the bitfield and are treated as untrackable (returns <c>false</c>).</remarks>
        private bool RecordReceivedForAck(ushort seq)
        {
            if (!_anyReceived)
            {
                _anyReceived = true;
                _highestReceivedSeq = seq;
                _ackBitfield = 0;
                return true;
            }

            int diff = Compare(seq, _highestReceivedSeq);
            if (diff > 0)
            {
                if (diff >= 64) _ackBitfield = 0;
                else _ackBitfield = (_ackBitfield << diff) | (1UL << (diff - 1));
                _highestReceivedSeq = seq;
                return true;
            }
            if (diff == 0) return false;

            int back = -diff;
            if (back > 64) return false;
            ulong bit = 1UL << (back - 1);
            if ((_ackBitfield & bit) != 0) return false;
            _ackBitfield |= bit;
            return true;
        }

        /// <summary>
        /// Emits a single coalesced ACK datagram (cumulative sequence + selective bitfield) if any acks are pending,
        /// snapshotting the ack state under lock and sending outside it. Called once per tick so received traffic is
        /// acknowledged in batches rather than one ACK per packet.
        /// </summary>
        /// <returns>A task that completes when the ACK has been written, or immediately if there is nothing to acknowledge.</returns>
        /// <remarks>The ACK buffer is rented from <see cref="ArrayPool{T}"/> and returned in a finally block.</remarks>
        private async Task FlushAckAsync()
        {
            if (_disposed || !_ackPending) return;
            _ackPending = false;

            ushort ackSeq;
            ulong bitfield;
            lock (_lock)
            {
                if (!_anyReceived) return;
                ackSeq = _highestReceivedSeq;
                bitfield = _ackBitfield;
            }

            var buf = ArrayPool<byte>.Shared.Rent(UdpDatagram.AckSize);
            try
            {
                var len = UdpDatagram.WriteAck(buf, _channel, ackSeq, bitfield);
                await _sendRaw(buf, len).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ── Retransmit + ACK tick (driven by the shared TimerScheduler) ─────────────
        /// <summary>
        /// Scheduler callback: triggers one retransmit/ACK-flush pass without blocking the shared loop. Guarded so
        /// overlapping ticks don't run the pass concurrently. This is the engine behind both reliability guarantees
        /// (eventual delivery) and ACK batching, now multiplexed onto one process-wide loop.
        /// </summary>
        private void OnTick()
        {
            if (_disposed) return;
            // Idle fast-path: with no ACK owed and nothing in flight there is nothing to retransmit or flush, so
            // skip spawning TickAsync entirely (no async state machine, no scratch work) on a quiet channel.
            if (!_ackPending)
            {
                lock (_lock)
                {
                    if (_unacked.Count == 0) return;
                }
            }
            _ = TickAsync();
        }

        /// <summary>Runs a single retransmit + ACK-flush pass; the <see cref="_ticking"/> guard prevents overlap.</summary>
        /// <returns>A task that completes once the pass finishes.</returns>
        private async Task TickAsync()
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
            try
            {
                await RetransmitAsync().ConfigureAwait(false);
                await FlushAckAsync().ConfigureAwait(false);
            }
            catch { }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        /// <summary>
        /// Resends every unacked packet whose ACK has not arrived within the timeout, incrementing each one's
        /// retransmit count. If any packet exceeds the retransmit cap, declares the link failed: drops all unacked
        /// packets, frees their window slots (so blocked senders don't deadlock), and invokes the failure callback.
        /// </summary>
        /// <returns>A task that completes once due retransmissions have been written, or once failure has been signalled.</returns>
        /// <remarks>State is snapshotted under <see cref="_lock"/> and the actual socket writes happen outside the lock to avoid blocking other threads on I/O.</remarks>
        private async Task RetransmitAsync()
        {
            var now = MonotonicClock.Timestamp;
            List<byte[]>? resend = null;
            var failed = false;

            lock (_lock)
            {
                // First pass: read-only scan collecting only the due sequences into a reused scratch list
                // (no per-tick List<ushort> allocation of all keys, and no mutation during enumeration).
                _dueScratch.Clear();
                foreach (var kv in _unacked)
                    if (MonotonicClock.ElapsedMs(kv.Value.LastSentTicks) >= _config.UdpReliableAckTimeoutMs)
                        _dueScratch.Add(kv.Key);

                // Second pass: bump/resend each due packet (value-update on an existing key; never adds/removes here).
                foreach (var seq in _dueScratch)
                {
                    var p = _unacked[seq];
                    p.Retransmits++;
                    if (p.Retransmits > _config.UdpReliableMaxRetransmits)
                    {
                        failed = true;
                        break;
                    }
                    p.LastSentTicks = now;
                    _unacked[seq] = p;
                    (resend ??= new List<byte[]>()).Add(p.Datagram);
                }
            }

            if (failed)
            {
                // Give up: drop the unacked packets and release their window slots so any
                // sender blocked on a full window is unblocked instead of deadlocking.
                ReleaseAllWindowSlots();
                _onFailure?.Invoke();
                return;
            }

            if (resend != null)
                foreach (var dg in resend)
                {
                    _config.Metrics.IncrementReliableRetransmits();
                    await _sendRaw(dg, dg.Length).ConfigureAwait(false);
                }
        }

        // Releases one window slot per still-unacked packet and clears the buffer.
        // Used on give-up and on dispose so blocked SendAsync callers never hang forever.
        /// <summary>
        /// Abandons all outstanding unacked packets and releases one send-window slot for each, then clears the
        /// buffer. Used on retransmit give-up and on dispose so that any <see cref="SendAsync"/> caller blocked on a
        /// full window is released instead of hanging forever.
        /// </summary>
        /// <remarks>Takes <see cref="_lock"/> internally; the <see cref="SemaphoreFullException"/> guard stops if the semaphore is already at capacity.</remarks>
        private void ReleaseAllWindowSlots()
        {
            lock (_lock)
            {
                var n = _unacked.Count;
                _unacked.Clear();
                for (int i = 0; i < n; i++)
                {
                    try { _windowSlots.Release(); }
                    catch (SemaphoreFullException) { break; }
                }
            }
        }

        /// <summary>
        /// Shuts the channel down: marks it disposed (stopping sends, inbound handling, and the tick loop), releases
        /// all window slots so blocked senders unblock, and cancels/disposes the tick-loop cancellation source.
        /// </summary>
        /// <remarks>Idempotent — a second call is a no-op. After dispose, <see cref="SendAsync"/> throws and inbound/ACK callbacks become no-ops.</remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TimerScheduler.Shared.Unschedule(_tickId);
            ReleaseAllWindowSlots();
        }
    }
}
