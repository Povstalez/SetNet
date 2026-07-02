using System;
using System.Buffers.Binary;

namespace SetNet.Protocol
{
    /// <summary>
    /// One decoded unified-protocol envelope: the <see cref="Kind"/>, the target <see cref="Channel"/> and
    /// <see cref="Op"/> within it, a correlation id linking a reply to its request, and the opaque
    /// <see cref="Body"/>. Only this thin fixed header is defined by the protocol; the body is whatever the module
    /// (or app) put there — either hand-framed bytes (serializer-agnostic control messages) or a value serialized
    /// with the app's <see cref="SetNet.Messaging.SetNetSerializer"/> (typed convenience path).
    /// </summary>
    internal readonly struct ProtocolEnvelope
    {
        /// <summary>The envelope role (send/request/reply/event/error).</summary>
        public readonly ProtocolKind Kind;

        /// <summary>The channel id (which module/feature this belongs to) — see <see cref="Channels"/>.</summary>
        public readonly ushort Channel;

        /// <summary>The operation or event id within the channel (module-assigned, typically a small enum).</summary>
        public readonly ushort Op;

        /// <summary>Process-unique id linking a <see cref="ProtocolKind.Reply"/>/<see cref="ProtocolKind.Error"/> back to its <see cref="ProtocolKind.Request"/>; 0 for one-way sends/events.</summary>
        public readonly int Corr;

        /// <summary>The opaque payload (never null after decode; empty when there is no body).</summary>
        public readonly byte[] Body;

        /// <summary>Creates an envelope from its parts.</summary>
        public ProtocolEnvelope(ProtocolKind kind, ushort channel, ushort op, int corr, byte[]? body)
        {
            Kind = kind;
            Channel = channel;
            Op = op;
            Corr = corr;
            Body = body ?? Array.Empty<byte>();
        }

        // Fixed little-endian header: [byte kind][ushort channel][ushort op][int corr], then the raw body.
        private const int HeaderSize = 1 + 2 + 2 + 4;

        /// <summary>
        /// Encodes the envelope to a single byte array. The result is sent as a <c>byte[]</c> message so it rides
        /// over any configured <c>ISerializer</c> without needing type attributes — exactly like the RPC envelope.
        /// </summary>
        public byte[] Encode()
        {
            var body = Body ?? Array.Empty<byte>();
            var buffer = new byte[HeaderSize + body.Length];
            buffer[0] = (byte)Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), Channel);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(3, 2), Op);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5, 4), Corr);
            Buffer.BlockCopy(body, 0, buffer, HeaderSize, body.Length);
            return buffer;
        }

        /// <summary>Decodes an envelope produced by <see cref="Encode"/>.</summary>
        /// <exception cref="ProtocolException">If the frame is too short to contain the header.</exception>
        public static ProtocolEnvelope Decode(byte[] frame)
        {
            if (frame == null || frame.Length < HeaderSize)
                throw new ProtocolException("Malformed protocol envelope.");

            var kind = (ProtocolKind)frame[0];
            var channel = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
            var op = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3, 2));
            var corr = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(5, 4));
            var body = new byte[frame.Length - HeaderSize];
            Buffer.BlockCopy(frame, HeaderSize, body, 0, body.Length);
            return new ProtocolEnvelope(kind, channel, op, corr, body);
        }
    }
}
