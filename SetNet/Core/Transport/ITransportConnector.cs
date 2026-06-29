using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// Client-side dialer that establishes an outbound <see cref="ITransportConnection"/> to a server.
    /// It is the client-facing half of the transport abstraction (mirrored by
    /// <see cref="ITransportListener"/> on the server) and is produced by
    /// <see cref="TransportFactory.CreateConnector"/> based on the configured
    /// <see cref="TransportType"/>. Implementations encapsulate the transport-specific connect
    /// procedure: TCP performs a connect-with-timeout and obtains the stream; UDP binds a socket and
    /// runs the handshake.
    /// </summary>
    public interface ITransportConnector
    {
        /// <summary>
        /// Connects to the remote endpoint described by <paramref name="config"/> and returns a ready,
        /// framed connection. This is the single entry point a client uses to obtain a live transport;
        /// any handshake/negotiation is completed before the task resolves.
        /// </summary>
        /// <param name="config">Connection settings (host, port, transport type, timeouts) describing the server to dial.</param>
        /// <param name="ct">A token used to cancel the connection attempt (including any timeout/handshake wait).</param>
        /// <returns>A connected <see cref="ITransportConnection"/> ready to send and receive messages.</returns>
        Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default);
    }
}
