using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>Client-side UDP connection (owns its socket, runs its own receive loop).</summary>
    /// <remarks>
    /// Created by <see cref="UdpClientConnector"/> after a successful handshake. Unlike TCP, there is a
    /// single connected <see cref="UdpClient"/> per connection on the client side, so this type both
    /// owns the socket and runs the demux loop that turns inbound datagrams into <see cref="TransportMessage"/>
    /// items. Reliable delivery is delegated to an optional <see cref="ReliabilityChannel"/>; unreliable
    /// sends go straight onto the wire. All public members are safe to call from any thread; sends are
    /// serialized by an internal lock and the inbound queue is single-consumer.
    /// </remarks>
    internal sealed class UdpClientConnection : ITransportConnection
    {
        /// <summary>The connected UDP socket owned by this connection; all sends/receives go through it.</summary>
        private readonly UdpClient _udp;

        /// <summary>Connection settings (limits, loss simulation, reliability toggle) captured at construction.</summary>
        private readonly Configuration _config;

        /// <summary>Session token identifying this UDP flow; echoed in the disconnect datagram on close.</summary>
        private readonly Guid _token;

        /// <summary>Single-consumer queue feeding <see cref="ReceiveAsync"/> with decoded, ready-to-dispatch messages (capacity-bounded for OOM protection).</summary>
        private readonly AsyncQueue<TransportMessage> _inbound;

        /// <summary>Serializes concurrent <see cref="UdpClient.SendAsync(byte[], int)"/> calls, which are not safe to overlap.</summary>
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        /// <summary>Per-channel reliability layer (ack/retransmit/ordering); empty when reliability is disabled.</summary>
        private readonly ReliabilityChannelSet _reliability;

        /// <summary>Source of randomness used only to decide which inbound datagrams to drop under simulated packet loss.</summary>
        private readonly Random _rng = new Random();

        /// <summary>Set once the connection has been closed; gates the receive loop, dispatch, and send paths.</summary>
        private volatile bool _closed;

        /// <summary>
        /// Wraps an already-connected, handshaked <see cref="UdpClient"/> as a transport connection and
        /// immediately starts pumping inbound datagrams. Spins up the reliability channel when enabled.
        /// </summary>
        /// <param name="udp">The connected UDP socket whose handshake has already completed; ownership transfers to this instance.</param>
        /// <param name="config">Connection configuration controlling limits, reliability, and loss simulation.</param>
        /// <param name="token">The session token established during the handshake; reused for the disconnect frame.</param>
        /// <remarks>The receive loop is started fire-and-forget; failures terminate the loop and complete the inbound queue rather than propagating.</remarks>
        public UdpClientConnection(UdpClient udp, Configuration config, Guid token)
        {
            _udp = udp;
            _config = config;
            _token = token;
            _inbound = new AsyncQueue<TransportMessage>(config.MaxInboundQueue);

            _reliability = new ReliabilityChannelSet(config, SendRawAsync, _inbound, Close);

            _ = ReceiveLoopAsync();
        }

        /// <summary>Gets a value indicating whether the connection is still open (i.e. <see cref="Close"/> has not been called).</summary>
        public bool IsConnected => !_closed;

        /// <summary>Gets the transport kind this connection implements; always <see cref="TransportType.Udp"/>.</summary>
        public TransportType Transport => TransportType.Udp;

        /// <summary>
        /// Sends an application message, routing it through the reliability channel for reliable delivery
        /// or framing it directly as an unreliable datagram otherwise. This is the connection's primary
        /// outbound entry point.
        /// </summary>
        /// <param name="type">The application-defined message type identifier.</param>
        /// <param name="payload">The serialized message body to transmit.</param>
        /// <param name="delivery">Whether to send reliably (acked/retransmitted) or unreliably (fire-and-forget).</param>
        /// <param name="channel">Reliable-channel index (0..<see cref="Configuration.UdpReliableChannels"/>-1); selects an independent ordered stream. Ignored for unreliable sends.</param>
        /// <param name="ct">Token to cancel a reliable send while it awaits send-window space; ignored on the unreliable path.</param>
        /// <returns>A task that completes once the message has been handed to the socket (unreliable) or queued for reliable delivery.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the connection is closed, or if reliable delivery is requested while reliability is disabled in configuration.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the framed unreliable datagram would exceed <see cref="Configuration.UdpMaxDatagramPayload"/>.</exception>
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

            // Frame into a pooled buffer to avoid a per-send allocation on the hot path.
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
        /// Asynchronously waits for and returns the next inbound application message, transparently
        /// including messages delivered out of the reliability channel.
        /// </summary>
        /// <param name="ct">Token used to cancel the wait.</param>
        /// <returns>The next decoded message, or <c>null</c> once the connection has closed and its inbound queue is drained.</returns>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var (ok, message) = await _inbound.DequeueAsync(ct).ConfigureAwait(false);
            return ok ? message : (TransportMessage?)null;
        }

        /// <summary>No-op: UDP datagrams are sent immediately (no batching layer).</summary>
        /// <returns>A completed task.</returns>
        public Task FlushAsync() => Task.CompletedTask;

        /// <summary>
        /// Sends raw datagram bytes over the socket under the send lock, swallowing send errors.
        /// Shared by the unreliable path and the reliability channel (passed as its send delegate),
        /// which is why it tolerates failures: UDP is best-effort and a dropped send is not fatal.
        /// </summary>
        /// <param name="datagram">Buffer containing the datagram bytes; only the first <paramref name="count"/> bytes are sent (the buffer may be pooled/oversized).</param>
        /// <param name="count">The number of leading bytes in <paramref name="datagram"/> to transmit.</param>
        /// <returns>A task that completes after the send attempt, whether or not it succeeded.</returns>
        /// <remarks>Acquires <see cref="_sendLock"/> to serialize overlapping sends, which <see cref="UdpClient"/> does not support concurrently.</remarks>
        private async Task SendRawAsync(byte[] datagram, int count)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await _udp.SendAsync(datagram, count).ConfigureAwait(false); }
            catch { /* unreliable transport: drop on send error */ }
            finally { _sendLock.Release(); }
        }

        /// <summary>
        /// Background loop that continuously receives datagrams from the socket and dispatches them,
        /// optionally dropping a configurable percentage to simulate inbound packet loss for testing.
        /// Runs for the lifetime of the connection and completes the inbound queue when it ends.
        /// </summary>
        /// <returns>A task representing the running loop; it terminates when the socket closes or an error occurs.</returns>
        /// <remarks>Started fire-and-forget from the constructor. Socket-closed exceptions are expected during shutdown and are swallowed.</remarks>
        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_closed)
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    if (_config.UdpSimulatedLossPercent > 0 && _rng.Next(100) < _config.UdpSimulatedLossPercent)
                        continue; // simulated inbound loss
                    Dispatch(result.Buffer);
                }
            }
            catch { /* socket closed */ }
            finally { _inbound.Complete(); }
        }

        /// <summary>
        /// Demultiplexes a single received datagram by its leading <see cref="PacketKind"/> byte and
        /// routes it: unreliable messages go to the inbound queue, reliable/ack frames go to the
        /// reliability channel, and a disconnect frame closes the connection.
        /// </summary>
        /// <param name="dg">The raw datagram bytes as received from the socket.</param>
        /// <remarks>No-ops if the connection is already closed (to avoid touching a disposed reliability channel or completed queue) or if the datagram is empty. Unknown kinds are silently ignored.</remarks>
        private void Dispatch(byte[] dg)
        {
            if (_closed) return; // don't touch a disposed reliability channel / completed queue
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
                    Close();
                    break;
            }
        }

        /// <summary>
        /// Closes the connection: tears down the reliability channel, sends a best-effort disconnect
        /// datagram so the peer can release its flow promptly, closes the socket, and completes the
        /// inbound queue so any pending <see cref="ReceiveAsync"/> returns end-of-stream.
        /// </summary>
        /// <remarks>
        /// Idempotent — safe to call multiple times and from multiple paths (explicit close, received
        /// disconnect, receive-loop teardown). The disconnect send and socket close are wrapped in
        /// try/catch because failures during teardown are expected and harmless.
        /// </remarks>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _reliability.Dispose();
            try
            {
                // Best-effort FIN so the server can free the flow without waiting for a timeout.
                var bye = UdpDatagram.BuildToken(PacketKind.Disconnect, _token);
                _ = _udp.SendAsync(bye, bye.Length);
            }
            catch { }
            try { _udp.Close(); } catch { }
            _inbound.Complete();
        }

        /// <summary>
        /// Releases all resources held by the connection by closing it and disposing the send lock.
        /// Implements <see cref="IDisposable"/> so the connection can participate in <c>using</c> scopes
        /// and deterministic cleanup.
        /// </summary>
        public void Dispose()
        {
            Close();
            _sendLock.Dispose();
        }
    }
}
