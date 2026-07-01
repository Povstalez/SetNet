using SetNet.Config;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// Factory for a custom transport, plugged in via <see cref="Configuration.CustomTransport"/> when
    /// <see cref="Configuration.TransportType"/> is <see cref="TransportType.Custom"/>. Lets external packages
    /// (WebSockets, WebRTC, QUIC, Steam sockets, …) add a transport without modifying the core — everything above
    /// the transport (handlers, RPC, rooms, auth) keeps working unchanged.
    /// </summary>
    public interface ITransportProvider
    {
        /// <summary>Builds the client-side dialer for this transport.</summary>
        /// <param name="config">The connection configuration.</param>
        ITransportConnector CreateConnector(Configuration config);

        /// <summary>Builds the server-side acceptor for this transport.</summary>
        /// <param name="config">The bind configuration.</param>
        ITransportListener CreateListener(Configuration config);
    }
}
