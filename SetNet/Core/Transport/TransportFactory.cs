using System;
using SetNet.Config;
using SetNet.Core.Transport.Both;
using SetNet.Core.Transport.Tcp;
using SetNet.Core.Transport.Udp;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// Central factory that maps the configured <see cref="Configuration.TransportType"/> onto the
    /// concrete connector (client side) and listener (server side) implementations. Keeping the
    /// selection in one place lets the rest of the library depend only on the
    /// <see cref="ITransportConnector"/> / <see cref="ITransportListener"/> abstractions and stay
    /// transport-agnostic; it is the single seam where TCP, UDP and Both wiring is chosen.
    /// </summary>
    internal static class TransportFactory
    {
        /// <summary>
        /// Builds the client-side dialer appropriate for the configured transport. Used by the client
        /// when establishing an outbound connection.
        /// </summary>
        /// <param name="config">The configuration whose <see cref="Configuration.TransportType"/> selects the connector.</param>
        /// <returns>A connector matching the requested transport (TCP, UDP, or Both).</returns>
        /// <exception cref="NotSupportedException">Thrown when the configured transport has no corresponding connector.</exception>
        public static ITransportConnector CreateConnector(Configuration config)
        {
            switch (config.TransportType)
            {
                case TransportType.Tcp: return new TcpConnector();
                case TransportType.Udp: return new UdpClientConnector();
                case TransportType.Both: return new BothConnector();
                default: throw new NotSupportedException($"Transport '{config.TransportType}' is not supported.");
            }
        }

        /// <summary>
        /// Builds the server-side acceptor appropriate for the configured transport. Used by the server
        /// when it starts listening for inbound connections.
        /// </summary>
        /// <param name="config">The configuration whose <see cref="Configuration.TransportType"/> selects the listener (and supplies bind details).</param>
        /// <returns>A listener matching the requested transport.</returns>
        /// <exception cref="NotSupportedException">
        /// Thrown when the configured transport has no corresponding listener. Note that
        /// <see cref="TransportType.Both"/> is intentionally not handled here: the Both server is composed
        /// from a TCP listener plus a UDP socket elsewhere rather than from a single listener.
        /// </exception>
        public static ITransportListener CreateListener(Configuration config)
        {
            switch (config.TransportType)
            {
                case TransportType.Tcp: return new TcpListenerAdapter(config);
                case TransportType.Udp: return new UdpServerListener(config);
                default: throw new NotSupportedException($"Transport '{config.TransportType}' is not supported.");
            }
        }
    }
}
