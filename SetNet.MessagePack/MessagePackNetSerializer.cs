using SetNet.Messaging;

namespace SetNet.MessagePack
{
    /// <summary>
    /// MessagePack-backed <see cref="ISerializer"/> for SetNet. Encodes/decodes message payloads with the
    /// <c>UntrustedData</c> security profile, which adds hash-collision protection and recursion-depth limits to
    /// mitigate deserialization denial-of-service attacks on payloads arriving off the network.
    /// </summary>
    /// <remarks>
    /// Register it once at startup, before connecting:
    /// <code>SetNetSerializer.Default = new MessagePackNetSerializer();</code>
    /// or per connection via <c>Configuration.Serializer</c>. Stateless and thread-safe (the shared options are
    /// immutable). Messages must be MessagePack-serializable — annotate DTOs with <c>[MessagePackObject]</c> and
    /// <c>[Key(n)]</c> (or use <c>[MessagePackObject(true)]</c> for key-as-name).
    /// </remarks>
    public sealed class MessagePackNetSerializer : ISerializer
    {
        // Payloads come off the network, so (de)serialize with the UntrustedData security profile
        // (hash-collision protection and depth limits) to mitigate deserialization DoS.
        private static readonly global::MessagePack.MessagePackSerializerOptions Options =
            global::MessagePack.MessagePackSerializerOptions.Standard
                .WithSecurity(global::MessagePack.MessagePackSecurity.UntrustedData);

        /// <inheritdoc/>
        public byte[] Serialize<T>(T value)
            => global::MessagePack.MessagePackSerializer.Serialize(value, Options);

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data)
            => global::MessagePack.MessagePackSerializer.Deserialize<T>(data, Options);
    }
}
