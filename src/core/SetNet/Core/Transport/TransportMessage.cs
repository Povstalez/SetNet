namespace SetNet.Core.Transport
{
    /// <summary>
    /// A decoded application message — a message type identifier paired with its raw serialized
    /// payload — handed up from a transport connection to the higher layers. This is the unit that
    /// crosses the boundary between the transport (which only understands frames of bytes) and the
    /// message-routing layer (which dispatches on <see cref="Type"/>). It is deliberately immutable
    /// so a received message can be passed around safely without defensive copying.
    /// </summary>
    public readonly struct TransportMessage
    {
        /// <summary>
        /// The application-defined message type identifier used to route the payload to the correct
        /// handler. This is the same <see cref="ushort"/> the sender supplied when framing the message.
        /// </summary>
        public ushort Type { get; }

        /// <summary>
        /// The raw, still-serialized message body (typically MessagePack bytes). The transport does
        /// not interpret these bytes; deserialization happens in the handler that owns this type.
        /// </summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Creates an immutable decoded message from a type identifier and its payload bytes, typically
        /// constructed by a transport connection after it has reassembled a complete frame.
        /// </summary>
        /// <param name="type">The application-defined message type identifier used for routing.</param>
        /// <param name="payload">The raw serialized message body extracted from the frame.</param>
        public TransportMessage(ushort type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }
    }
}
