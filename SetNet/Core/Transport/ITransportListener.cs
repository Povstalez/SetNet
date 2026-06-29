using System;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// Server-side acceptor that turns inbound traffic into a stream of logical
    /// <see cref="ITransportConnection"/> instances, one per remote peer. It is the server-facing half
    /// of the transport abstraction (mirrored by <see cref="ITransportConnector"/> on the client) and
    /// is produced by <see cref="TransportFactory.CreateListener"/>. Implementations hide the
    /// transport-specific accept mechanics: TCP wraps <c>TcpListener.AcceptTcpClientAsync</c>; UDP
    /// wraps the single shared socket with packet demultiplexing and a handshake, yielding one
    /// connection per handshaked endpoint.
    /// </summary>
    /// <remarks>
    /// Lifecycle: call <see cref="Start"/> before accepting, pump <see cref="AcceptAsync"/> in a loop,
    /// then <see cref="Stop"/> to cease listening; <see cref="IDisposable.Dispose"/> releases the
    /// underlying socket. Existing accepted connections are independent objects and are not closed by
    /// stopping the listener.
    /// </remarks>
    public interface ITransportListener : IDisposable
    {
        /// <summary>
        /// Begins listening for inbound connections (binds/opens the underlying socket). Must be called
        /// before <see cref="AcceptAsync"/> will produce connections.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops listening for new connections so that <see cref="AcceptAsync"/> unblocks and returns
        /// <c>null</c>. Does not tear down connections that were already accepted.
        /// </summary>
        void Stop();

        /// <summary>
        /// Waits for and returns the next fully established peer. This is the server's accept loop
        /// primitive; it abstracts away whether establishment means a TCP accept or a completed UDP
        /// handshake.
        /// </summary>
        /// <param name="ct">A token used to cancel the wait for an incoming connection.</param>
        /// <returns>
        /// The newly established connection wrapped in an <see cref="AcceptedConnection"/>, or <c>null</c>
        /// when the listener is stopping/stopped and no further connections will be produced.
        /// </returns>
        Task<AcceptedConnection?> AcceptAsync(CancellationToken ct = default);
    }
}
