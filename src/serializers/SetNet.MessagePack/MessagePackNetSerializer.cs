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
    /// <code>SetNetSerializer.Use(new MessagePackNetSerializer());</code>
    /// Stateless and thread-safe (the shared options are immutable). Messages must be MessagePack-serializable —
    /// annotate DTOs with <c>[MessagePackObject]</c> and <c>[Key(n)]</c> (or use <c>[MessagePackObject(true)]</c>
    /// for key-as-name).
    /// </remarks>
    public sealed class MessagePackNetSerializer : ISerializer, IMemorySerializer, IBinaryFrameSerializer
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

        /// <inheritdoc/>
        /// <remarks>
        /// MessagePack reads from a <c>ReadOnlyMemory&lt;byte&gt;</c> natively, so a payload sitting inside a larger
        /// received frame is decoded where it lies — no intermediate array. This is the path SetNet takes for client
        /// push events.
        /// </remarks>
        public T Deserialize<T>(System.ReadOnlyMemory<byte> data)
            => global::MessagePack.MessagePackSerializer.Deserialize<T>(data, Options);

        // ── IBinaryFrameSerializer ─────────────────────────────────────────
        //
        // MessagePack encodes byte[] as the bin family: a 2/3/5-byte header
        // (bin8/bin16/bin32 with a big-endian length) followed by the raw
        // bytes. That makes Serialize<byte[]>(p) exactly [header][p], which is
        // the contract this capability promises. Guarded by unit tests that
        // compare against MessagePackSerializer.Serialize for edge lengths.

        /// <inheritdoc/>
        public int MeasureBinaryFrameHeader(int payloadLength)
        {
            if (payloadLength < 0) throw new System.ArgumentOutOfRangeException(nameof(payloadLength));
            if (payloadLength <= byte.MaxValue) return 2;    // 0xc4 len8
            if (payloadLength <= ushort.MaxValue) return 3;  // 0xc5 len16 (big-endian)
            return 5;                                        // 0xc6 len32 (big-endian)
        }

        /// <inheritdoc/>
        public int WriteBinaryFrameHeader(System.Span<byte> destination, int payloadLength)
        {
            if (payloadLength < 0) throw new System.ArgumentOutOfRangeException(nameof(payloadLength));

            if (payloadLength <= byte.MaxValue)
            {
                destination[0] = 0xc4;
                destination[1] = (byte)payloadLength;
                return 2;
            }

            if (payloadLength <= ushort.MaxValue)
            {
                destination[0] = 0xc5;
                destination[1] = (byte)(payloadLength >> 8);
                destination[2] = (byte)payloadLength;
                return 3;
            }

            destination[0] = 0xc6;
            destination[1] = (byte)(payloadLength >> 24);
            destination[2] = (byte)(payloadLength >> 16);
            destination[3] = (byte)(payloadLength >> 8);
            destination[4] = (byte)payloadLength;
            return 5;
        }
    }
}
