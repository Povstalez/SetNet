namespace SetNet.Messaging
{
    /// <summary>
    /// Process-wide serialization seam. <see cref="Default"/> is the serializer the library uses wherever a
    /// connection does not override it (and the one the static <see cref="Serialize{T}"/>/<see cref="Deserialize{T}"/>
    /// helpers delegate to). It defaults to MessagePack.
    /// </summary>
    /// <remarks>
    /// To switch the whole application to another format, assign a custom <see cref="ISerializer"/> ONCE at
    /// startup, before connecting:
    /// <code>SetNetSerializer.Default = new MyJsonSerializer();</code>
    /// Use <see cref="Serialize{T}"/>/<see cref="Deserialize{T}"/> inside message handlers (which have no
    /// connection reference) to stay serializer-agnostic. For a per-connection serializer, set
    /// <c>Configuration.Serializer</c> instead, and deserialize on the server via
    /// <c>peer.CurrentPeerInfo.Config.Serializer</c>. Both ends of a connection must use the same serializer.
    /// </remarks>
    public static class SetNetSerializer
    {
        /// <summary>
        /// The active serializer used when a connection does not specify its own. Set this once at startup.
        /// Defaults to <see cref="MessagePackNetSerializer"/>.
        /// </summary>
        public static ISerializer Default { get; set; } = new MessagePackNetSerializer();

        /// <summary>Serializes a message via <see cref="Default"/>.</summary>
        /// <typeparam name="T">The message type.</typeparam>
        /// <param name="value">The message to serialize.</param>
        /// <returns>The serialized payload.</returns>
        public static byte[] Serialize<T>(T value) => Default.Serialize(value);

        /// <summary>Deserializes a payload via <see cref="Default"/>. Prefer this in handlers over a format-specific helper.</summary>
        /// <typeparam name="T">The target message type.</typeparam>
        /// <param name="data">The received payload.</param>
        /// <returns>The decoded message.</returns>
        public static T Deserialize<T>(byte[] data) => Default.Deserialize<T>(data);
    }
}
