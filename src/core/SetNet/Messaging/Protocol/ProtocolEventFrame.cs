using System;
using System.Buffers.Binary;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// Builds the complete wire payload of one unified-protocol push event —
    /// <c>[serializer byte[]-wrap header][envelope header][body]</c> — in a single buffer.
    /// </summary>
    /// <remarks>
    /// The legacy publish path allocated three buffers per event: the body, the encoded envelope
    /// (header + body copy), and the serializer wrap of that envelope (another full copy) produced inside
    /// <c>SendAsync&lt;byte[]&gt;</c>. When the configured serializer implements
    /// <see cref="IBinaryFrameSerializer"/> this encoder writes the wrap header and the envelope directly
    /// into one final array, which is byte-identical to the legacy output — the receiving side needs no
    /// change and mixed-version peers interoperate. Serializers without the capability transparently fall
    /// back to the legacy two-step encoding.
    /// <para>
    /// A frame built once can be pushed to any number of peers via
    /// <see cref="ProtocolPeerExtensions.PublishFrameAsync"/> — the fan-out cost per recipient is then just
    /// the transport write, with zero per-recipient allocations above the transport.
    /// </para>
    /// </remarks>
    public static class ProtocolEventFrame
    {
        private const int EnvelopeHeaderSize = 1 + 2 + 2 + 4;

        /// <summary>
        /// Encodes one push event for (<paramref name="channel"/>, <paramref name="op"/>) into a single
        /// ready-to-send buffer using the serializer of <paramref name="runtime"/>.
        /// </summary>
        /// <param name="runtime">The runtime whose serializer defines the <c>byte[]</c> wire framing.</param>
        /// <param name="channel">The protocol channel id.</param>
        /// <param name="op">The event id within the channel.</param>
        /// <param name="body">The serialized event body (may be empty).</param>
        /// <returns>The complete wire payload for <c>ProtocolTypes.Envelope</c>, ready for a raw send.</returns>
        public static byte[] Encode(SetNetRuntime runtime, ushort channel, ushort op, ReadOnlySpan<byte> body)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            int envelopeLength = EnvelopeHeaderSize + body.Length;

            if (runtime.Serializer is IBinaryFrameSerializer framer)
            {
                int headerSize = framer.MeasureBinaryFrameHeader(envelopeLength);
                var frame = new byte[headerSize + envelopeLength];
                int written = framer.WriteBinaryFrameHeader(frame, envelopeLength);
                if (written != headerSize)
                    throw new InvalidOperationException(
                        $"{runtime.Serializer.GetType().Name} wrote {written} wrap-header bytes but measured {headerSize}.");

                var envelope = frame.AsSpan(headerSize);
                envelope[0] = (byte)ProtocolKind.Event;
                BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(1, 2), channel);
                BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(3, 2), op);
                BinaryPrimitives.WriteInt32LittleEndian(envelope.Slice(5, 4), 0);
                body.CopyTo(envelope.Slice(EnvelopeHeaderSize));
                return frame;
            }

            // Legacy path for serializers without the framing capability: encode the envelope, then let the
            // serializer wrap it. Two extra buffers, but works with ANY ISerializer and stays wire-identical.
            var env = new ProtocolEnvelope(ProtocolKind.Event, channel, op, 0, body.ToArray());
            return runtime.Serialize(env.Encode());
        }
    }
}
