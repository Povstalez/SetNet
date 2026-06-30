using System;
using System.Buffers;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Server-side view of one UDP peer. Has no socket of its own — it sends through the shared
    /// <see cref="UdpServerListener"/> socket and is fed inbound datagrams by the listener's demux.
    /// </summary>
    internal sealed class UdpServerConnection : ITransportConnection
    {
        /// <summary>The owning listener that holds the shared socket; all outbound sends are routed through it.</summary>
        private readonly UdpServerListener _listener;

        /// <summary>Transport configuration controlling datagram size limits, reliability, and expiry behaviour.</summary>
        private readonly Configuration _config;

        /// <summary>Buffer of decoded application messages awaiting consumption by <see cref="ReceiveAsync"/> (capacity-bounded for OOM protection).</summary>
        private readonly AsyncQueue<TransportMessage> _inbound;

        /// <summary>
        /// Optional reliability layer (sequencing, ACKs, retransmit). Non-null only when
        /// <see cref="Configuration.UdpReliabilityEnabled"/> is set; otherwise reliable delivery is unsupported.
        /// </summary>
        private readonly ReliabilityChannelSet _reliability;

        /// <summary>Monotonic timestamp of the last inbound datagram, used by the listener's idle-expiry sweep.</summary>
        private long _lastReceivedTicks;

        /// <summary>Set once the connection has been closed; guards against use-after-close on the queue/reliability channel.</summary>
        private volatile bool _closed;

        /// <summary>The remote peer's endpoint; serves as the demux key in the listener's per-endpoint table.</summary>
        public IPEndPoint Remote { get; }

        /// <summary>The session token exchanged during the handshake, identifying this logical UDP session.</summary>
        public Guid Token { get; }

        /// <summary>
        /// Creates a server-side handle for a single UDP peer that has completed (or is completing) its handshake.
        /// Records the initial activity timestamp and, when reliability is enabled, spins up the reliability channel
        /// wired to send through the shared socket and to fail the connection if delivery is abandoned.
        /// </summary>
        /// <param name="listener">The owning listener whose shared socket carries this connection's traffic.</param>
        /// <param name="remote">The remote endpoint this connection represents.</param>
        /// <param name="token">The handshake-negotiated session token identifying this peer.</param>
        /// <param name="config">Transport configuration (datagram limits, reliability toggles, expiry timeout).</param>
        /// <param name="reliabilityEnabled">When false (Both mode, where reliable rides TCP), the UDP leg builds no reliability channels.</param>
        public UdpServerConnection(UdpServerListener listener, IPEndPoint remote, Guid token, Configuration config, bool reliabilityEnabled = true)
        {
            _listener = listener;
            Remote = remote;
            Token = token;
            _config = config;
            _inbound = new AsyncQueue<TransportMessage>(config.MaxInboundQueue);
            Interlocked.Exchange(ref _lastReceivedTicks, MonotonicClock.Timestamp);

            _reliability = new ReliabilityChannelSet(config, SendRawAsync, _inbound, Close, reliabilityEnabled);
        }

        /// <summary>Gets a value indicating whether the connection is still open (has not been closed or expired).</summary>
        public bool IsConnected => !_closed;

        /// <summary>Gets the transport kind for this connection, always <see cref="TransportType.Udp"/>.</summary>
        public TransportType Transport => TransportType.Udp;

        /// <summary>
        /// Sends an application message to this peer using the requested delivery guarantee. Unreliable messages
        /// are framed and written directly to the socket; reliable messages are handed to the reliability channel
        /// for sequencing, ACK tracking, and retransmission.
        /// </summary>
        /// <param name="type">The application-defined message type identifier written into the datagram header.</param>
        /// <param name="payload">The serialized message body to transmit.</param>
        /// <param name="delivery">Whether to send with best-effort (<see cref="DeliveryMethod.Unreliable"/>) or guaranteed (<see cref="DeliveryMethod.Reliable"/>) delivery.</param>
        /// <param name="channel">Reliable-channel index (0..<see cref="Configuration.UdpReliableChannels"/>-1); selects an independent ordered stream. Ignored for unreliable sends.</param>
        /// <param name="ct">Token to cancel the send (notably while waiting for a reliable send-window slot).</param>
        /// <returns>A task that completes once the datagram has been written to the socket (or queued for reliable delivery).</returns>
        /// <exception cref="InvalidOperationException">The connection is closed, or reliable delivery was requested without <see cref="Configuration.UdpReliabilityEnabled"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An unreliable datagram (header + payload) would exceed <see cref="Configuration.UdpMaxDatagramPayload"/>.</exception>
        /// <remarks>The send buffer is rented from <see cref="ArrayPool{T}"/> and returned in a finally block to avoid GC pressure on the hot path.</remarks>
        public async Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            if (_closed) throw new InvalidOperationException("UDP connection is closed.");

            if (delivery == DeliveryMethod.Reliable)
            {
                await _reliability.SendAsync(channel, type, payload, ct).ConfigureAwait(false);
                return;
            }

            var total = UdpDatagram.UnreliableHeader + payload.Length;
            if (total > _config.UdpMaxDatagramPayload)
                throw new ArgumentOutOfRangeException(nameof(payload),
                    $"UDP datagram ({total}B) exceeds UdpMaxDatagramPayload ({_config.UdpMaxDatagramPayload}B).");

            var buf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                UdpDatagram.WriteUnreliable(buf, type, payload);
                await SendRawAsync(buf, total).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>
        /// Awaits the next fully decoded application message received from this peer. Unreliable messages
        /// surface as soon as they arrive; reliable messages surface after the reliability channel has
        /// applied de-duplication and (if configured) in-order reassembly.
        /// </summary>
        /// <param name="ct">Token to cancel the wait.</param>
        /// <returns>The next message, or <c>null</c> once the connection is closed and the inbound queue is drained.</returns>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var (ok, message) = await _inbound.DequeueAsync(ct).ConfigureAwait(false);
            return ok ? message : (TransportMessage?)null;
        }

        /// <summary>No-op: UDP datagrams are sent immediately (no batching layer).</summary>
        /// <returns>A completed task.</returns>
        public Task FlushAsync() => Task.CompletedTask;

        /// <summary>
        /// Writes an already-framed datagram to this peer through the listener's shared socket.
        /// Exists as the single egress point shared by both the unreliable path and the reliability channel.
        /// </summary>
        /// <param name="datagram">The buffer containing the framed datagram bytes.</param>
        /// <param name="count">The number of valid bytes in <paramref name="datagram"/> to send.</param>
        /// <returns>A task that completes when the bytes have been handed to the socket.</returns>
        private Task SendRawAsync(byte[] datagram, int count) => _listener.SendRawAsync(datagram, count, Remote);

        /// <summary>
        /// Handles a raw datagram delivered by the listener's single demux loop for this endpoint. Refreshes the
        /// activity timestamp and routes the datagram by packet kind: unreliable payloads are enqueued directly,
        /// reliable/ACK packets are forwarded to the reliability channel, and a disconnect packet closes the peer.
        /// </summary>
        /// <param name="dg">The raw datagram bytes as received from the socket.</param>
        /// <remarks>
        /// Invoked from the listener's receive loop thread. Returns immediately if the connection is already closed
        /// so it never touches a disposed reliability channel or a completed inbound queue.
        /// </remarks>
        public void OnDatagram(byte[] dg)
        {
            if (_closed) return; // don't touch a disposed reliability channel / completed queue
            Interlocked.Exchange(ref _lastReceivedTicks, MonotonicClock.Timestamp);
            if (dg.Length < 1) return;
            switch (dg[0])
            {
                case PacketKind.Unreliable:
                    if (UdpDatagram.TryParseUnreliable(dg, out var type, out var payload)
                        && !_inbound.TryEnqueue(new TransportMessage(type, payload)))
                        _config.Metrics.IncrementInboundDropped(); // best-effort: shed unreliable load when the queue is full
                    break;
                case PacketKind.Reliable:
                    _reliability.OnReliableDatagram(dg);
                    break;
                case PacketKind.Ack:
                    _reliability.OnAck(dg);
                    break;
                case PacketKind.Disconnect:
                    // Require the session token so a spoofed-source 1-byte 0x40 (or a wrong-token datagram)
                    // cannot tear down this peer. Only a sender that knows the handshake token can disconnect it.
                    if (UdpDatagram.TryParseToken(dg, out var byeToken) && byeToken == Token)
                        Close();
                    break;
            }
        }

        /// <summary>
        /// Determines whether the peer has gone silent for longer than the allowed idle window, used by the
        /// listener's expiry sweep to reap connections from clients that vanished without a clean disconnect.
        /// </summary>
        /// <param name="expiryMs">The maximum tolerated idle interval, in milliseconds, since the last received datagram.</param>
        /// <returns><c>true</c> if no datagram has arrived within <paramref name="expiryMs"/>; otherwise <c>false</c>.</returns>
        public bool IsExpired(int expiryMs)
            => MonotonicClock.ElapsedMs(Interlocked.Read(ref _lastReceivedTicks)) > expiryMs;

        /// <summary>
        /// Tears down the connection: marks it closed, disposes the reliability channel, removes it from the
        /// listener's demux table, makes a best-effort attempt to notify the peer with a disconnect datagram,
        /// and completes the inbound queue so any pending <see cref="ReceiveAsync"/> returns <c>null</c>.
        /// </summary>
        /// <remarks>Idempotent — repeated calls after the first are no-ops. The disconnect notification is best-effort and any send failure is swallowed.</remarks>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _reliability.Dispose();
            _listener.RemoveConnection(this);
            try
            {
                var bye = UdpDatagram.BuildToken(PacketKind.Disconnect, Token);
                _ = _listener.SendRawAsync(bye, bye.Length, Remote);
            }
            catch { }
            _inbound.Complete();
        }

        /// <summary>Disposes the connection by closing it; provided so the connection can be used in <c>using</c> scopes and uniform cleanup paths.</summary>
        public void Dispose() => Close();
    }
}
