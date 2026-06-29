namespace SetNet.Core.Transport
{
    /// <summary>
    /// Selects which network transport the client/server uses. This is the top-level switch the
    /// <see cref="TransportFactory"/> reads to decide which connector and listener implementation
    /// to build, and it determines how a <see cref="DeliveryMethod"/> on an individual message is
    /// routed onto the wire.
    /// </summary>
    public enum TransportType
    {
        /// <summary>
        /// Reliable, ordered TCP stream (default; the library's original behaviour). Every message
        /// is guaranteed to arrive in order, and <see cref="DeliveryMethod"/> is ignored.
        /// </summary>
        Tcp,

        /// <summary>
        /// Connectionless UDP datagrams with an emulated connection layer (handshake, liveness and an
        /// optional reliability channel) layered on top so the rest of the stack can treat it like a
        /// connection.
        /// </summary>
        Udp,

        /// <summary>
        /// Both TCP and UDP at once for a single logical peer: messages flagged
        /// <see cref="DeliveryMethod.Reliable"/> travel over TCP while
        /// <see cref="DeliveryMethod.Unreliable"/> messages travel over UDP, letting callers pick the
        /// guarantee per message.
        /// </summary>
        Both
    }
}
