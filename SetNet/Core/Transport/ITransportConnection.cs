using System;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// One logical, framed connection to a single remote peer and the central abstraction of the
    /// transport layer: everything above it (clients, peers, message routing) talks to a connection
    /// without knowing whether the bytes travel over TCP, UDP, or both. The implementation owns all
    /// transport-specific framing — TCP wraps a <c>NetworkStream</c> with length-prefix reassembly;
    /// UDP wraps a datagram socket plus a handshake and an optional reliability layer — and exposes a
    /// uniform "send a typed message / receive a typed message" surface.
    /// </summary>
    /// <remarks>
    /// Implementations are not required to be thread-safe for concurrent <see cref="SendAsync"/> /
    /// <see cref="ReceiveAsync"/> calls of the same kind; the intended usage is a single reader loop
    /// pumping <see cref="ReceiveAsync"/> while sends happen from the application thread.
    /// <see cref="IDisposable.Dispose"/> releases the underlying socket/stream and should be treated
    /// as the hard teardown, whereas <see cref="Close"/> is the graceful, intentional shutdown.
    /// </remarks>
    public interface ITransportConnection : IDisposable
    {
        /// <summary>
        /// Gets a value indicating whether the peer is currently considered alive. For TCP this
        /// reflects the socket's connected state; for UDP it is <c>false</c> once the emulated
        /// connection has expired (liveness timeout) or been closed.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the underlying transport kind backing this connection, so callers can reason about the
        /// guarantees available (for example, whether <see cref="DeliveryMethod.Unreliable"/> is meaningful).
        /// </summary>
        TransportType Transport { get; }

        /// <summary>
        /// Frames a single application message and writes it to the peer. This is the only send path;
        /// the caller chooses the per-message guarantee via <paramref name="delivery"/>.
        /// </summary>
        /// <param name="type">The application-defined message type identifier to stamp onto the frame for routing on the far side.</param>
        /// <param name="payload">The raw serialized message body to transmit.</param>
        /// <param name="delivery">
        /// The requested delivery guarantee. Honoured by transports that distinguish channels (UDP, and
        /// the UDP side of <see cref="TransportType.Both"/>); TCP ignores it because it is always
        /// reliable and ordered.
        /// </param>
        /// <param name="channel">
        /// For reliable UDP, the independent reliable channel (0-based) this message rides, so a loss on one
        /// channel doesn't head-of-line block another. Ignored by TCP and for unreliable delivery.
        /// </param>
        /// <param name="ct">A token used to cancel the send operation.</param>
        /// <returns>A task that completes once the message has been handed to the underlying transport.</returns>
        Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default);

        /// <summary>
        /// Awaits and returns the next complete application message from the peer, hiding all framing
        /// and reassembly so callers receive whole messages rather than byte fragments.
        /// </summary>
        /// <param name="ct">A token used to cancel the receive operation while waiting for data.</param>
        /// <returns>
        /// The next decoded <see cref="TransportMessage"/>, or <c>null</c> when the peer closed the
        /// connection gracefully (TCP EOF / UDP close), signalling the read loop to stop.
        /// </returns>
        /// <exception cref="System.IO.IOException">Thrown on a transport-level read failure (broken socket, malformed frame).</exception>
        Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default);

        /// <summary>
        /// Flushes any buffered outbound data when send batching is enabled; a no-op otherwise. Call after
        /// composing a tick's worth of messages so they are written to the socket in a single operation.
        /// </summary>
        /// <returns>A task that completes once buffered data has been written.</returns>
        Task FlushAsync();

        /// <summary>
        /// Closes the connection gracefully and intentionally (for example, the server kicking a peer
        /// or a client logging out), as opposed to an error-driven teardown. Safe to call more than
        /// once; subsequent calls are no-ops.
        /// </summary>
        void Close();
    }
}
