namespace SetNet.Messaging
{
    /// <summary>
    /// Central helper that converts strongly-typed messages to and from their MessagePack byte representation
    /// for transmission over the wire. It is the single serialization entry point used by handlers and the
    /// transport layer, and it hardens deserialization against malicious input by applying the
    /// <c>UntrustedData</c> security profile to every operation.
    /// </summary>
    /// <remarks>
    /// All members are static and stateless; the shared <see cref="Options"/> instance is immutable, so the
    /// type is safe to use concurrently from multiple threads.
    /// </remarks>
    public static class MessagePackSerializer
    {
        /// <summary>
        /// Shared, immutable serializer options applied to every serialize/deserialize call. Built from the
        /// standard resolver but configured with <see cref="MessagePack.MessagePackSecurity.UntrustedData"/> so
        /// that payloads arriving off the network get hash-collision protection and recursion-depth limits,
        /// mitigating deserialization denial-of-service attacks.
        /// </summary>
        // Payloads come off the network, so deserialize with the UntrustedData security profile
        // (hash-collision protection and depth limits) to mitigate deserialization DoS.
        private static readonly MessagePack.MessagePackSerializerOptions Options =
            MessagePack.MessagePackSerializerOptions.Standard
                .WithSecurity(MessagePack.MessagePackSecurity.UntrustedData);

        /// <summary>
        /// Serializes a typed message into a MessagePack-encoded byte array ready to be framed and sent.
        /// </summary>
        /// <typeparam name="T">The message type being serialized; must be supported by the MessagePack resolver.</typeparam>
        /// <param name="message">The message instance to encode.</param>
        /// <returns>The MessagePack binary representation of <paramref name="message"/>.</returns>
        public static byte[] Serialize<T>(T message)
            => MessagePack.MessagePackSerializer.Serialize(message, Options);

        /// <summary>
        /// Deserializes a MessagePack-encoded payload back into a strongly-typed message, applying the
        /// untrusted-data security profile because the input typically originates from a remote endpoint.
        /// </summary>
        /// <typeparam name="T">The target message type to reconstruct.</typeparam>
        /// <param name="data">The MessagePack-encoded payload (already unframed, without the length/type header).</param>
        /// <returns>The decoded message instance of type <typeparamref name="T"/>.</returns>
        public static T Deserialize<T>(byte[] data)
            => MessagePack.MessagePackSerializer.Deserialize<T>(data, Options);
    }
}
