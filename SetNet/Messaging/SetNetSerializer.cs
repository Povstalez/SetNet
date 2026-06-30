using System;

namespace SetNet.Messaging
{
    /// <summary>
    /// Process-wide serialization façade — the single entry point the whole library uses to turn messages into
    /// wire bytes and back. Register a serializer once at startup with <see cref="Use"/>, then everything (the
    /// send path and the typed-handler receive path) flows through <see cref="Serialize{T}"/> /
    /// <see cref="Deserialize{T}"/>.
    /// </summary>
    /// <remarks>
    /// The core library ships with NO serializer bundled, so you must choose one before sending or receiving —
    /// for example the MessagePack adapter from the <c>SetNet.MessagePack</c> package:
    /// <code>SetNetSerializer.Use(new MessagePackNetSerializer());</code>
    /// or any custom <see cref="ISerializer"/> (JSON, Protobuf, …). Until one is registered,
    /// <see cref="Serialize{T}"/> and <see cref="Deserialize{T}"/> throw an <see cref="InvalidOperationException"/>
    /// explaining what to do. Both ends of a connection must use the same serializer.
    /// </remarks>
    public static class SetNetSerializer
    {
        /// <summary>The active serializer. Not exposed publicly — callers configure it via <see cref="Use"/> and use it via <see cref="Serialize{T}"/>/<see cref="Deserialize{T}"/>.</summary>
        private static ISerializer _serializer = new UnconfiguredSerializer();

        /// <summary>
        /// Registers the serializer the whole application uses. Call this ONCE at startup, before connecting.
        /// </summary>
        /// <param name="serializer">The serialization strategy (e.g. <c>MessagePackNetSerializer</c> or your own).</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="serializer"/> is <see langword="null"/>.</exception>
        public static void Use(ISerializer serializer)
            => _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

        /// <summary>The currently registered serializer. Internal so tests can save/restore it; external code uses <see cref="Use"/> / <see cref="Serialize{T}"/> / <see cref="Deserialize{T}"/>.</summary>
        internal static ISerializer Current => _serializer;

        /// <summary>Serializes a message with the registered serializer.</summary>
        /// <typeparam name="T">The message type.</typeparam>
        /// <param name="value">The message to serialize.</param>
        /// <returns>The serialized payload.</returns>
        public static byte[] Serialize<T>(T value) => _serializer.Serialize(value);

        /// <summary>Deserializes a payload with the registered serializer.</summary>
        /// <typeparam name="T">The target message type.</typeparam>
        /// <param name="data">The received payload.</param>
        /// <returns>The decoded message.</returns>
        public static T Deserialize<T>(byte[] data) => _serializer.Deserialize<T>(data);

        /// <summary>
        /// Placeholder serializer installed when none has been registered. Every operation throws an
        /// <see cref="InvalidOperationException"/> describing how to register a real serializer, so the failure
        /// is immediate and self-explanatory rather than a confusing null/empty payload downstream.
        /// </summary>
        private sealed class UnconfiguredSerializer : ISerializer
        {
            private const string Message =
                "No serializer configured. Register one once at startup — e.g. " +
                "'SetNetSerializer.Use(new MessagePackNetSerializer());' from the SetNet.MessagePack package, " +
                "or your own ISerializer implementation.";

            /// <inheritdoc/>
            public byte[] Serialize<T>(T value) => throw new InvalidOperationException(Message);

            /// <inheritdoc/>
            public T Deserialize<T>(byte[] data) => throw new InvalidOperationException(Message);
        }
    }
}
