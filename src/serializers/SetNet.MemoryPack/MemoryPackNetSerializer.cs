using MemoryPack;
using SetNet.Messaging;

namespace SetNet.MemoryPack
{
    /// <summary>
    /// A <see cref="ISerializer"/> backed by <c>MemoryPack</c> — an extremely fast, zero-encoding, source-generated
    /// binary serializer that is AOT/IL2CPP-friendly (no runtime reflection), making it a strong choice for Unity.
    /// Message types must be annotated with <c>[MemoryPackable]</c> and be <c>partial</c>. Register once at startup with
    /// <c>SetNetSerializer.Use(new MemoryPackNetSerializer())</c>; both ends must use the same serializer.
    /// </summary>
    public sealed class MemoryPackNetSerializer : ISerializer
    {
        /// <inheritdoc/>
        public byte[] Serialize<T>(T value) => MemoryPackSerializer.Serialize(value);

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data) => MemoryPackSerializer.Deserialize<T>(data)!;
    }
}
