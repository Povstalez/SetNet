using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport.Udp;

namespace SetNet.Core.Transport.Both
{
    /// <summary>
    /// Client-side composite over a TCP connection and (optionally) a UDP connection.
    /// Reliable traffic and heartbeat ride TCP; unreliable rides UDP when available.
    /// Inbound frames from both channels are merged into one stream. TCP is the lifeline:
    /// when it ends, the whole connection ends; a UDP failure alone is tolerated.
    /// </summary>
    internal sealed class BothConnection : ITransportConnection
    {
        /// <summary>The reliable TCP channel. Carries reliable traffic and heartbeat, and acts as the lifeline whose closure ends the whole connection.</summary>
        private readonly ITransportConnection _tcp;

        /// <summary>The optional UDP channel for unreliable traffic. <c>null</c> when UDP was unavailable and the connection degraded to TCP-only.</summary>
        private readonly ITransportConnection? _udp;

        /// <summary>Single inbound queue into which frames from both the TCP and UDP pumps are merged, so consumers see one unified stream.</summary>
        private readonly AsyncQueue<TransportMessage> _merged = new AsyncQueue<TransportMessage>();

        /// <summary>Set once the connection has been torn down; <c>volatile</c> so the value is observed promptly across the pump tasks and consumer threads.</summary>
        private volatile bool _closed;

        /// <summary>
        /// Composes a TCP and an optional UDP channel into a single transport connection and immediately
        /// starts background pumps that drain both channels into the merged inbound queue.
        /// </summary>
        /// <param name="tcp">The reliable TCP channel; serves as the connection lifeline.</param>
        /// <param name="udp">The optional UDP channel for unreliable traffic, or <c>null</c> to operate TCP-only.</param>
        /// <remarks>
        /// The pump tasks are fire-and-forget (assigned to discards). The TCP pump, when it ends, triggers
        /// <see cref="Close"/> so a dropped lifeline tears down UDP and completes the queue.
        /// </remarks>
        public BothConnection(ITransportConnection tcp, ITransportConnection? udp)
        {
            _tcp = tcp;
            _udp = udp;
            _ = PumpAsync(_tcp, isTcp: true);
            if (_udp != null) _ = PumpAsync(_udp, isTcp: false);
        }

        /// <summary>Gets a value indicating whether the connection is live: it has not been closed and the underlying TCP lifeline is still connected.</summary>
        public bool IsConnected => !_closed && _tcp.IsConnected;

        /// <summary>Gets the transport classification for this connection, always <see cref="TransportType.Both"/>.</summary>
        public TransportType Transport => TransportType.Both;

        /// <summary>
        /// Sends a framed message over the channel best suited to its delivery requirements: heartbeat and
        /// reliable traffic ride TCP; unreliable traffic prefers UDP when it is connected, otherwise falls back to TCP.
        /// </summary>
        /// <param name="type">The application/system message type identifier used to route the frame on receipt.</param>
        /// <param name="payload">The serialized message body to transmit.</param>
        /// <param name="delivery">The requested delivery guarantee; <see cref="DeliveryMethod.Unreliable"/> enables the UDP fast path.</param>
        /// <param name="channel">Reliable-UDP channel id; only meaningful when this routes to the UDP reliable path.</param>
        /// <param name="ct">Token used to cancel the send.</param>
        /// <returns>A task that completes when the chosen channel has accepted the frame for transmission.</returns>
        /// <remarks>Ping/Pong heartbeat frames are always pinned to TCP so liveness tracking is unaffected by UDP loss.</remarks>
        public Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            // Heartbeat always rides the reliable TCP lifeline.
            if (type == SystemMessageTypes.Ping || type == SystemMessageTypes.Pong)
                return _tcp.SendAsync(type, payload, delivery, channel, ct);

            if (delivery == DeliveryMethod.Unreliable && _udp != null && _udp.IsConnected)
                return _udp.SendAsync(type, payload, delivery, channel, ct);

            return _tcp.SendAsync(type, payload, delivery, channel, ct);
        }

        /// <summary>
        /// Awaits the next inbound message from either channel, transparently merged in arrival order, so callers
        /// consume one stream without knowing which underlying transport delivered each frame.
        /// </summary>
        /// <param name="ct">Token used to cancel the wait.</param>
        /// <returns>The next merged <see cref="TransportMessage"/>, or <c>null</c> when the connection has closed and the queue is drained.</returns>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var (ok, message) = await _merged.DequeueAsync(ct).ConfigureAwait(false);
            return ok ? message : (TransportMessage?)null;
        }

        /// <summary>Flushes the reliable TCP channel's batch buffer (UDP is never batched).</summary>
        /// <returns>A task that completes once the TCP batch has been written.</returns>
        public Task FlushAsync() => _tcp.FlushAsync();

        /// <summary>
        /// Background loop that continuously reads from one underlying channel and enqueues its frames into the
        /// merged queue, until the channel signals end-of-stream or the connection is closed.
        /// </summary>
        /// <param name="conn">The underlying TCP or UDP channel to drain.</param>
        /// <param name="isTcp">
        /// <c>true</c> if this pump is draining the TCP lifeline; when a TCP pump exits it tears the whole
        /// connection down, whereas a UDP pump exit is tolerated and leaves TCP running.
        /// </param>
        /// <returns>A task that completes when the pump stops (channel EOF, error, or close).</returns>
        /// <remarks>
        /// Receive exceptions are swallowed and treated as a normal channel end. Frames are not enqueued once
        /// <see cref="_closed"/> is set, to avoid pushing past the EOF already signalled to the consumer.
        /// </remarks>
        private async Task PumpAsync(ITransportConnection conn, bool isTcp)
        {
            try
            {
                while (!_closed)
                {
                    var msg = await conn.ReceiveAsync().ConfigureAwait(false);
                    if (msg == null) break;
                    if (_closed) break; // don't enqueue after the consumer has been signalled EOF
                    _merged.Enqueue(msg.Value);
                }
            }
            catch { /* channel ended */ }
            finally
            {
                // The TCP channel is the lifeline: when it ends, tear down the whole connection
                // (closing UDP and completing the queue). Losing UDP alone is tolerated.
                if (isTcp)
                    Close();
            }
        }

        /// <summary>
        /// Tears down the composite connection: closes both underlying channels and completes the merged queue so
        /// any pending <see cref="ReceiveAsync"/> observes end-of-stream. Idempotent — safe to call more than once.
        /// </summary>
        /// <remarks>Called both explicitly and implicitly when the TCP lifeline pump ends.</remarks>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _udp?.Close();
            _tcp.Close();
            _merged.Complete();
        }

        /// <summary>
        /// Closes the connection and disposes both underlying channels, releasing their sockets and unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Close();
            _tcp.Dispose();
            _udp?.Dispose();
        }
    }
}
