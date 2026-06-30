namespace SetNet.Messaging
{
    /// <summary>
    /// Pluggable serialization strategy: converts strongly-typed messages to and from the byte payload carried
    /// on the wire. The library default is <see cref="MessagePackNetSerializer"/> (MessagePack). Swap it
    /// process-wide via <see cref="SetNetSerializer.Default"/>, or per connection via
    /// <c>Configuration.Serializer</c>, to use JSON, Protobuf, or any custom format.
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
