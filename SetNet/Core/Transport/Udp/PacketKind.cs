namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Enumerates the leading "kind" byte that prefixes every UDP datagram on the wire. The receiver
    /// reads this first byte to demultiplex the datagram and decide how to parse the remaining bytes.
    /// Sits at the bottom of the UDP transport stack as the protocol's frame-type registry.
    /// </summary>
    /// <remarks>
    /// The numeric values are part of the wire protocol and must stay stable across versions; both
    /// peers are this same library, so the set is closed and self-consistent. The trailing comments
    /// next to each constant describe the byte layout that follows the kind byte.
    /// </remarks>
    internal static class PacketKind
    {
        /// <summary>Client → server connection request (SYN), carrying the 16-byte session token. Layout: <c>[kind][16-byte token]</c>.</summary>
        public const byte Handshake    = 0x01; // client → server: SYN  ([kind][16-byte token])

        /// <summary>Server → client handshake acknowledgement (SYN-ACK), echoing the token to confirm the flow. Layout: <c>[kind][16-byte token]</c>.</summary>
        public const byte HandshakeAck = 0x02; // server → client: SYN-ACK ([kind][16-byte token])

        /// <summary>Best-effort application message with no delivery guarantee. Layout: <c>[kind][2-byte type][payload]</c>.</summary>
        public const byte Unreliable   = 0x10; // [kind][2-byte type][payload]

        /// <summary>Sequenced application message that participates in the ack/retransmit reliability layer. Layout: <c>[kind][2-byte seq][2-byte type][payload]</c>.</summary>
        public const byte Reliable     = 0x20; // [kind][2-byte seq][2-byte type][payload]

        /// <summary>Acknowledgement for reliable messages, carrying the latest received sequence plus a 64-entry bitfield of recent acks. Layout: <c>[kind][2-byte ackSeq][8-byte bitfield]</c>.</summary>
        public const byte Ack          = 0x21; // [kind][2-byte ackSeq][8-byte bitfield]

        /// <summary>Best-effort connection teardown (FIN), carrying the session token. Layout: <c>[kind][16-byte token]</c>.</summary>
        public const byte Disconnect   = 0x40; // [kind][16-byte token] (best-effort FIN)
    }
}
