using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Core.Transport.Tcp
{
    /// <summary>
    /// <see cref="ITransportConnection"/> over a TCP <see cref="NetworkStream"/>. Reuses the
    /// existing <see cref="PacketBuilder"/> for length-prefix framing and stream reassembly.
    /// </summary>
    internal sealed class TcpConnection : ITransportConnection
    {
        /// <summary>The underlying TCP socket wrapper this connection owns and is responsible for closing.</summary>
        private readonly TcpClient _client;

        /// <summary>
        /// The duplex byte stream the connection reads and writes through. This is the raw
        /// <see cref="NetworkStream"/> for plaintext TCP, or an <c>SslStream</c> wrapping it when TLS is enabled.
        /// </summary>
        private readonly Stream _stream;

        /// <summary>
        /// Accumulates raw bytes from the stream and slices them back into complete length-prefixed frames,
        /// handling the case where a single socket read spans partial or multiple messages. The frame-size cap
        /// rejects oversized declared lengths to bound buffer growth.
        /// </summary>
        private readonly PacketBuilder _packetBuilder;

        /// <summary>
        /// Serializes concurrent <see cref="SendAsync"/> calls so frames are never interleaved on the wire.
        /// A binary semaphore (count 1) is used instead of <c>lock</c> because it supports async waiting.
        /// </summary>
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        /// <summary>Reusable scratch buffer for socket reads, sized from the configured buffer size to avoid per-read allocations.</summary>
        private readonly byte[] _buffer;

        /// <summary>
        /// Set once when the connection is closed. Marked <c>volatile</c> so the close signal is visible across
        /// the receive and send threads without additional synchronization.
        /// </summary>
        private volatile bool _closed;

        /// <summary>True when outbound frames are coalesced into <see cref="_batchBuffer"/> instead of written immediately.</summary>
        private readonly bool _batching;

        /// <summary>Accumulates framed bytes when batching; flushed in one write. Null when batching is off.</summary>
        private byte[]? _batchBuffer;

        /// <summary>Number of valid bytes currently buffered in <see cref="_batchBuffer"/>.</summary>
        private int _batchLength;

        /// <summary>Registration id of the auto-flush tick on the shared scheduler (when batching).</summary>
        private readonly long _flushId;

        /// <summary>Maximum time a single socket write may take before it is abandoned and the connection torn down; 0 disables the bound.</summary>
        private readonly int _sendTimeoutMs;

        /// <summary>
        /// Wraps an already-connected <see cref="TcpClient"/> as a transport connection, capturing its stream
        /// and allocating the read buffer. Used by both the client connector and the server-side acceptor.
        /// </summary>
        /// <param name="client">A connected TCP client whose lifetime this connection takes ownership of.</param>
        /// <param name="stream">The stream to read/write — the raw <see cref="NetworkStream"/> or an <c>SslStream</c> over it.</param>
        /// <param name="bufferSize">Size, in bytes, of the per-read scratch buffer; typically the configured socket buffer size.</param>
        /// <param name="maxFrameSize">Maximum accepted frame length in bytes (0 = unlimited); larger declared lengths close the connection.</param>
        /// <param name="sendBatching">When true, coalesce outbound frames and flush them together.</param>
        /// <param name="flushMs">Auto-flush interval (ms) for the batch buffer when batching is enabled.</param>
        /// <param name="sendTimeoutMs">Maximum time a single socket write may take before the connection is torn down; 0 disables the bound.</param>
        public TcpConnection(TcpClient client, Stream stream, int bufferSize, int maxFrameSize, bool sendBatching = false, int flushMs = 15, int sendTimeoutMs = 0)
        {
            _client = client;
            _stream = stream;
            _buffer = new byte[bufferSize];
            _packetBuilder = new PacketBuilder(maxFrameSize);
            _batching = sendBatching;
            _sendTimeoutMs = sendTimeoutMs;
            if (_batching)
            {
                _batchBuffer = new byte[bufferSize];
                _flushId = TimerScheduler.Shared.Schedule(flushMs < 1 ? 1 : flushMs, () => _ = FlushAsync());
            }
        }

        /// <summary>
        /// Gets a value indicating whether the connection is still usable, i.e. not explicitly closed and the
        /// underlying socket still reports a live connection.
        /// </summary>
        public bool IsConnected => !_closed && _client.Connected;

        /// <summary>Gets the transport kind, always <see cref="TransportType.Tcp"/> for this implementation.</summary>
        public TransportType Transport => TransportType.Tcp;

        /// <summary>
        /// Frames the payload with a length-prefixed header and writes it to the socket, serializing against
        /// other senders so frames stay intact on the wire.
        /// </summary>
        /// <param name="type">Message type identifier written into the frame header for handler routing on the receiver.</param>
        /// <param name="payload">The serialized message body to transmit.</param>
        /// <param name="delivery">Requested delivery guarantee; ignored because TCP is already reliable and ordered.</param>
        /// <param name="channel">Reliable-channel index; ignored on TCP, which is a single ordered stream.</param>
        /// <param name="ct">Token to cancel both the write-lock wait and the socket write.</param>
        /// <returns>A task that completes once the framed bytes have been handed to the socket.</returns>
        /// <remarks>
        /// Thread-safe: concurrent callers are serialized by <see cref="_writeLock"/>. The frame is built into a
        /// buffer rented from <see cref="ArrayPool{T}"/> to avoid a per-send allocation, and is always returned.
        /// </remarks>
        public async Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            // TCP is inherently reliable+ordered; delivery method and channel are irrelevant (single stream).
            var total = PacketBuilder.HeaderSize + payload.Length;

            if (_batching)
            {
                // Append the frame to the batch buffer; the auto-flush tick (or an explicit FlushAsync) writes it.
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    EnsureBatchCapacity(total);
                    PacketBuilder.WriteFrame(_batchBuffer!, _batchLength, type, payload, payload.Length);
                    _batchLength += total;
                }
                finally
                {
                    _writeLock.Release();
                }
                return;
            }

            // Immediate write: frame into a pooled buffer to avoid a per-send allocation.
            var frame = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                PacketBuilder.WriteFrame(frame, type, payload, payload.Length);
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // On a send timeout, TimedWriteHeldAsync disposes the stream and awaits the abandoned write
                    // BEFORE returning — so by the time we release the lock and return the buffer below, no live
                    // write references the frame and a second sender will hit a disposed stream, never a
                    // concurrent write on a live one.
                    await TimedWriteHeldAsync(frame, total, ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        /// <summary>
        /// Writes <paramref name="count"/> bytes of <paramref name="buf"/> to the stream while the caller holds
        /// <see cref="_writeLock"/>, bounding the write by <see cref="_sendTimeoutMs"/> when configured. On timeout
        /// it tears the connection down (disposing the stream, which faults the parked write) and awaits that
        /// write to completion — all while the lock is still held — so the buffer is free and no second writer can
        /// start on a live stream, then throws <see cref="TimeoutException"/>. With no timeout configured it is a
        /// plain awaited write.
        /// </summary>
        /// <param name="buf">The buffer to write from (offset 0).</param>
        /// <param name="count">Number of bytes to write.</param>
        /// <param name="ct">Caller cancellation token.</param>
        /// <exception cref="TimeoutException">The write did not complete within <see cref="_sendTimeoutMs"/>.</exception>
        private async Task TimedWriteHeldAsync(byte[] buf, int count, CancellationToken ct)
        {
            if (_sendTimeoutMs <= 0)
            {
                await _stream.WriteAsync(buf, 0, count, ct).ConfigureAwait(false);
                return;
            }

            // Link the timeout to the caller's token so the delay timer is released promptly on the hot path
            // (cancelled below) rather than lingering armed for the full timeout on every healthy send.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writeTask = _stream.WriteAsync(buf, 0, count, timeoutCts.Token);
            var delay = Task.Delay(_sendTimeoutMs, timeoutCts.Token);
            var finished = await Task.WhenAny(writeTask, delay).ConfigureAwait(false);

            if (finished != writeTask)
            {
                // Stuck peer: the write is parked in the kernel. Dispose the stream NOW (still under the lock) to
                // fault that write, then await it so the buffer is no longer referenced and the lock is released
                // only once no live write remains. A subsequent sender then hits a disposed stream — never a
                // concurrent write on a live one.
                Teardown(flushBatch: false);
                try { await writeTask.ConfigureAwait(false); } catch { /* expected: faulted by the dispose/cancel */ }
                throw new TimeoutException($"TCP send timed out after {_sendTimeoutMs}ms.");
            }

            // Write won: cancel the timeout so its timer is released immediately, then surface any write fault.
            timeoutCts.Cancel();
            await writeTask.ConfigureAwait(false);
        }

        /// <summary>Grows the batch buffer (doubling) so it can hold <paramref name="extra"/> more bytes. Caller holds the write lock.</summary>
        /// <param name="extra">Number of additional bytes about to be appended.</param>
        private void EnsureBatchCapacity(int extra)
        {
            if (_batchLength + extra <= _batchBuffer!.Length) return;
            var newSize = _batchBuffer.Length * 2;
            while (newSize < _batchLength + extra) newSize *= 2;
            var grown = new byte[newSize];
            Buffer.BlockCopy(_batchBuffer, 0, grown, 0, _batchLength);
            _batchBuffer = grown;
        }

        /// <summary>
        /// Writes any buffered frames to the socket in a single operation. No-op when batching is off or nothing
        /// is buffered. Called automatically by the scheduler and on demand by the client/peer.
        /// </summary>
        /// <returns>A task that completes once the buffered bytes have been written.</returns>
        public async Task FlushAsync()
        {
            if (!_batching || _closed) return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_batchLength > 0)
                {
                    // Consume the batch up-front: if the write throws (broken connection), the bytes are
                    // discarded rather than left in the buffer where the next send would append after them
                    // and the next flush would re-send the partially-written prefix, corrupting the stream.
                    var len = _batchLength;
                    _batchLength = 0;
                    try
                    {
                        await TimedWriteHeldAsync(_batchBuffer!, len, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException) { /* TimedWriteHeldAsync already disposed the stream under the lock */ }
                    catch { /* connection error; the receive loop will tear down. Batch already discarded. */ }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Reads the next complete message from the stream, transparently reassembling frames that arrive
        /// split across multiple socket reads or batched together in a single read.
        /// </summary>
        /// <param name="ct">Token to cancel the pending socket read.</param>
        /// <returns>
        /// The next decoded <see cref="TransportMessage"/>, or <c>null</c> when the peer closes the connection
        /// gracefully (a zero-byte read / EOF).
        /// </returns>
        /// <remarks>
        /// Not safe to call concurrently with itself: a single receive loop should own this connection's reads.
        /// Buffered frames left over from a previous read are drained before touching the socket again.
        /// </remarks>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            // A single socket read may yield several frames; drain the buffered ones first.
            // Decode straight to (type, payload) — one payload copy, no intermediate frame array.
            if (_packetBuilder.TryGetCompleteMessage(out var bufferedType, out var bufferedPayload))
                return new TransportMessage(bufferedType, bufferedPayload);

            while (true)
            {
                var bytesRead = await _stream.ReadAsync(_buffer, 0, _buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead == 0) return null; // graceful close / EOF

                _packetBuilder.AppendData(_buffer, 0, bytesRead); // no slice-copy

                if (_packetBuilder.TryGetCompleteMessage(out var type, out var payload))
                    return new TransportMessage(type, payload);
            }
        }

        /// <summary>
        /// Closes the underlying socket and marks this connection as no longer usable. Idempotent: a second
        /// call is a no-op, and any error from closing an already-dead socket is swallowed.
        /// </summary>
        /// <remarks>
        /// Setting <see cref="_closed"/> first causes any in-flight <see cref="ReceiveAsync"/> to observe the
        /// socket as closed and unwind, which is how the receive loop is signalled to stop.
        /// </remarks>
        public void Close() => Teardown(flushBatch: true);

        /// <summary>
        /// Closes the socket and marks the connection unusable. <paramref name="flushBatch"/> controls whether a
        /// best-effort flush of buffered frames is attempted first: true for a normal/graceful close, false when
        /// tearing down a connection whose peer is already stuck (a send timeout), where attempting another
        /// blocking write would just hang again.
        /// </summary>
        /// <param name="flushBatch">Whether to flush any buffered batch before closing.</param>
        private void Teardown(bool flushBatch)
        {
            if (_closed) return;
            _closed = true;
            if (_batching)
            {
                TimerScheduler.Shared.Unschedule(_flushId);
                if (flushBatch) FlushBatchOnClose(); // best-effort delivery of buffered frames before tearing down
            }
            try { _stream.Dispose(); } catch { /* disposes the SslStream when TLS is in use */ }
            try { _client.Close(); } catch { /* already closed */ }
        }

        /// <summary>
        /// Best-effort synchronous flush of any buffered frames during <see cref="Close"/>, so a graceful shutdown
        /// does not silently drop messages still sitting in the batch buffer. Bounded so shutdown never blocks
        /// indefinitely, and tolerant of an already-broken socket.
        /// </summary>
        private void FlushBatchOnClose()
        {
            if (!_writeLock.Wait(100)) return; // don't stall shutdown if a send/flush is mid-flight
            try
            {
                if (_batchLength > 0)
                {
                    _stream.Write(_batchBuffer!, 0, _batchLength);
                    _batchLength = 0;
                }
            }
            catch { /* socket already broken; nothing more we can do */ }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Releases all resources held by the connection: closes the socket via <see cref="Close"/> and disposes
        /// the write-lock semaphore.
        /// </summary>
        public void Dispose()
        {
            Close();
            _writeLock.Dispose();
        }
    }
}
