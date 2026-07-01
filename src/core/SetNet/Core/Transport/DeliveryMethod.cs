namespace SetNet.Core.Transport
{
    /// <summary>
    /// Per-message delivery guarantee requested by the caller. It is the application-level intent
    /// ("does this message matter if it's lost?") that the transport layer maps onto a concrete wire
    /// channel. Routing depends on the active <see cref="TransportType"/>: in
    /// <see cref="TransportType.Both"/> mode <see cref="Reliable"/> goes over TCP and
    /// <see cref="Unreliable"/> over UDP; in single-transport modes the flag selects a UDP channel (or
    /// is ignored entirely by TCP, which is always reliable).
    /// </summary>
    public enum DeliveryMethod
    {
        /// <summary>
        /// Guaranteed, ordered delivery. Carried by TCP, or by the UDP reliability layer when the UDP
        /// transport's reliable channel is in use. Choose this for state that must not be dropped
        /// (logins, chat, inventory changes).
        /// </summary>
        Reliable,

        /// <summary>
        /// Fire-and-forget delivery that may be lost, reordered or duplicated (raw UDP). Choose this
        /// for high-frequency, perishable data such as position updates where the newest packet
        /// supersedes any that were missed.
        /// </summary>
        Unreliable
    }
}
