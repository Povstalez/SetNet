using System;

namespace SetNet.Messaging
{
    /// <summary>
    /// Process-wide serialization seam. <see cref="Default"/> is the serializer the library uses wherever a
    /// connection does not override it (and the one the static <see cref="Serialize{T}"/>/<see cref="Deserialize{T}"/>
    /// helpers delegate to).
    /// </summary>
    /// <remarks>
    /// The core library ships with NO serializer bundled, so you must choose one before sending or receiving.
    /// Assign an <see cref="ISerializer"/> ONCE at startup, before connecting — for example the MessagePack
    /// adapter from the <c>SetNet.MessagePack</c> package:
    /// <code>SetNetSerializer.Default = new MessagePackNetSerializer();</code>
    /// or any custom implementation (JSON, Protobuf, …). Until one is set, <see cref="Serialize{T}"/> and
    /// <see cref="Deserialize{T}"/> throw an <see cref="InvalidOperationException"/> explaining what to do.
    /// This single process-wide instance is what the library uses everywhere — both the send path and the
    /// <see cref="Serialize{T}"/>/<see cref="Deserialize{T}"/> facade used inside message handlers. Both ends of
    /// a connection must use the same serializer.
    /// </remarks>
    public static class SetNetSerializer
    {
        /// <summary>
        /// The active serializer used when a connection does not specify its own. Set this once at startup.
        /// Defaults to an unconfigured serializer that throws on use, since the core library bundles no format —
        /// install <c>SetNet.MessagePack</c> (or supply your own <see cref="ISerializer"/>) and assign it here.
        /// </summary>
        public static ISerializer Default { get; set; } = new UnconfiguredSerializer();

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

        /// <summary>
        /// Placeholder serializer installed when none has been configured. Every operation throws an
        /// <see cref="InvalidOperationException"/> describing how to register a real serializer, so the failure
        /// is immediate and self-explanatory rather than a confusing null/empty payload downstream.
        /// </summary>
        private sealed class UnconfiguredSerializer : ISerializer
        {
            private const string Message =
                "No serializer configured. Set SetNetSerializer.Default (or Configuration.Serializer) once at " +
                "startup — e.g. 'SetNetSerializer.Default = new MessagePackNetSerializer();' from the " +
                "SetNet.MessagePack package, or your own ISerializer implementation.";

            /// <inheritdoc/>
            public byte[] Serialize<T>(T value) => throw new InvalidOperationException(Message);

            /// <inheritdoc/>
            public T Deserialize<T>(byte[] data) => throw new InvalidOperationException(Message);
        }
    }
}
