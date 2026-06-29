using System;
using System.Net;

namespace SetNet.Core.Transport
{
    /// <summary>
    /// The result of <see cref="ITransportListener.AcceptAsync"/>: a freshly established
    /// <see cref="ITransportConnection"/> bundled with the metadata the server needs to place it. The
    /// extra fields exist primarily for <see cref="TransportType.Both"/> mode, where an incoming UDP
    /// flow must be matched to the TCP peer that already represents the same logical client via a
    /// shared binding token.
    /// </summary>
    public sealed class AcceptedConnection
    {
        /// <summary>
        /// The newly accepted connection, ready to send and receive messages.
        /// </summary>
        public ITransportConnection Connection { get; }

        /// <summary>
        /// The binding token that correlates this connection with an existing peer. In
        /// <see cref="TransportType.Both"/> mode the client presents the same token over TCP and UDP so
        /// the server can associate the two flows; <see cref="Guid.Empty"/> when no correlation applies.
        /// </summary>
        public Guid ConnectionToken { get; }

        /// <summary>
        /// The remote endpoint the connection originated from, when the transport can supply it (notably
        /// UDP, where the source address/port identifies the flow). May be <c>null</c> when not applicable.
        /// </summary>
        public IPEndPoint? RemoteEndPoint { get; }

        /// <summary>
        /// Wraps a newly accepted connection together with its correlation token and remote endpoint so
        /// the server can register and (in Both mode) pair it correctly.
        /// </summary>
        /// <param name="connection">The established transport connection to surface to the server.</param>
        /// <param name="connectionToken">
        /// The token correlating this connection with an existing peer; defaults to <see cref="Guid.Empty"/>
        /// when no correlation is needed (single-transport modes).
        /// </param>
        /// <param name="remoteEndPoint">The originating remote endpoint, or <c>null</c> if the transport does not expose one.</param>
        public AcceptedConnection(ITransportConnection connection, Guid connectionToken = default, IPEndPoint? remoteEndPoint = null)
        {
            Connection = connection;
            ConnectionToken = connectionToken;
            RemoteEndPoint = remoteEndPoint;
        }
    }
}
