namespace SetNet.Messaging
{
    /// <summary>
    /// Default <see cref="ISerializer"/> implementation backed by MessagePack with the <c>UntrustedData</c>
    /// security profile. Byte-for-byte compatible with the static <see cref="MessagePackSerializer"/> helper, so
    /// it is the zero-config default for every connection.
    /// </summary>
    public sealed class MessagePackNetSerializer : ISerializer
    {
        /// <inheritdoc/>
        public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value);

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data);
    }
}
