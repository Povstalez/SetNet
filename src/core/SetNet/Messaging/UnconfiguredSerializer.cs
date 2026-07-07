using System;

namespace SetNet.Messaging
{
    /// <summary>Serializer placeholder used until an application chooses a concrete serializer.</summary>
    internal sealed class UnconfiguredSerializer : ISerializer
    {
        private const string Message =
            "No serializer configured. Register one once at startup — e.g. " +
            "'SetNetSerializer.Use(new MessagePackNetSerializer());' from the SetNet.MessagePack package, " +
            "or configure a scoped runtime with 'new SetNetRuntime().UseSerializer(...)'.";

        /// <inheritdoc/>
        public byte[] Serialize<T>(T value) => throw new InvalidOperationException(Message);

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data) => throw new InvalidOperationException(Message);
    }
}
