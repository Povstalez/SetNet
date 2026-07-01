namespace SetNet.Messaging
{
    /// <summary>
    /// Pluggable serialization strategy: converts strongly-typed messages to and from the byte payload carried
    /// on the wire. The core library bundles no serializer; install one (e.g. <c>MessagePackNetSerializer</c>
    /// from the <c>SetNet.MessagePack</c> package) or supply your own (JSON, Protobuf, or any custom format),
    /// then register it process-wide via <see cref="SetNetSerializer.Use"/>.
    /// </summary>
    /// <remarks>
    /// Both ends of a connection must use the SAME serializer. Implementations must be thread-safe (they are
    /// called from the send path and from message handlers on multiple threads).
    /// </remarks>
    public interface ISerializer
    {
        /// <summary>Serializes a message into the byte payload to be framed and sent.</summary>
        /// <typeparam name="T">The message type.</typeparam>
        /// <param name="value">The message instance to encode.</param>
        /// <returns>The serialized payload (without the transport's length/type header).</returns>
        byte[] Serialize<T>(T value);

        /// <summary>Deserializes a received byte payload back into a message of type <typeparamref name="T"/>.</summary>
        /// <typeparam name="T">The target message type to reconstruct.</typeparam>
        /// <param name="data">The received payload (already unframed).</param>
        /// <returns>The decoded message.</returns>
        T Deserialize<T>(byte[] data);
    }
}
