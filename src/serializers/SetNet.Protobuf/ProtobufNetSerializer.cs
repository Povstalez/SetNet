using System.IO;
using SetNet.Messaging;

namespace SetNet.Protobuf
{
    /// <summary>
    /// A <see cref="ISerializer"/> backed by <c>protobuf-net</c> (Protocol Buffers). Compact and, crucially,
    /// **cross-language** — pair it with non-.NET clients (Go, C++, JS, …) that speak protobuf. Message types are
    /// annotated with <c>[ProtoContract]</c>/<c>[ProtoMember(n)]</c>. Register once at startup with
    /// <c>SetNetSerializer.Use(new ProtobufNetSerializer())</c>; both ends must use the same serializer.
    /// </summary>
    public sealed class ProtobufNetSerializer : ISerializer
    {
        /// <inheritdoc/>
        public byte[] Serialize<T>(T value)
        {
            using var ms = new MemoryStream();
            ProtoBuf.Serializer.Serialize(ms, value);
            return ms.ToArray();
        }

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data)
        {
            using var ms = new MemoryStream(data);
            return ProtoBuf.Serializer.Deserialize<T>(ms);
        }
    }
}
