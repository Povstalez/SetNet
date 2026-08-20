using System;
using System.Linq;
using SetNet.MessagePack;
using SetNet.Messaging;
using SetNet.Protocol;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>
    /// The single-buffer event-frame encoder must be byte-identical to the legacy
    /// "encode envelope, then Serialize&lt;byte[]&gt; it" path — that identity is what lets 1.6.1 servers
    /// talk to older clients without any receive-side change. These tests pin the contract for the
    /// MessagePack serializer (all three bin header widths) and for serializers without the capability.
    /// </summary>
    public class ProtocolEventFrameTests
    {
        /// <summary>Serializer without <see cref="IBinaryFrameSerializer"/> — forces the fallback path.</summary>
        private sealed class PlainMessagePack : ISerializer
        {
            private readonly MessagePackNetSerializer _inner = new MessagePackNetSerializer();
            public byte[] Serialize<T>(T value) => _inner.Serialize(value);
            public T Deserialize<T>(byte[] data) => _inner.Deserialize<T>(data);
        }

        private static byte[] LegacyEncode(ISerializer serializer, ushort channel, ushort op, byte[] body)
        {
            // The exact pre-1.6.1 wire construction: envelope header + body, wrapped as a byte[] message.
            var envelope = new byte[9 + body.Length];
            envelope[0] = 3; // ProtocolKind.Event
            envelope[1] = (byte)channel;
            envelope[2] = (byte)(channel >> 8);
            envelope[3] = (byte)op;
            envelope[4] = (byte)(op >> 8);
            // corr = 0 → bytes 5..8 stay zero.
            Buffer.BlockCopy(body, 0, envelope, 9, body.Length);
            return serializer.Serialize(envelope);
        }

        private static byte[] Body(int length) =>
            Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();

        [Theory]
        [InlineData(0)]      // bin8, empty body
        [InlineData(1)]
        [InlineData(246)]    // envelope length 255 — bin8 upper edge
        [InlineData(247)]    // envelope length 256 — first bin16
        [InlineData(300)]
        [InlineData(65526)]  // envelope length 65535 — bin16 upper edge
        [InlineData(65527)]  // envelope length 65536 — first bin32
        [InlineData(100_000)]
        public void Encode_matches_legacy_serialize_wrap_for_messagepack(int bodyLength)
        {
            var serializer = new MessagePackNetSerializer();
            var runtime = new SetNetRuntime().UseSerializer(serializer);
            var body = Body(bodyLength);

            var fast = ProtocolEventFrame.Encode(runtime, channel: 61234, op: 42, body);
            var legacy = LegacyEncode(serializer, channel: 61234, op: 42, body);

            Assert.Equal(legacy, fast);
        }

        [Fact]
        public void Encode_falls_back_for_serializers_without_the_capability()
        {
            var serializer = new PlainMessagePack();
            var runtime = new SetNetRuntime().UseSerializer(serializer);
            var body = Body(500);

            var encoded = ProtocolEventFrame.Encode(runtime, channel: 7, op: 9, body);

            Assert.Equal(LegacyEncode(serializer, 7, 9, body), encoded);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        [InlineData(256)]
        [InlineData(65535)]
        [InlineData(65536)]
        public void Binary_frame_header_matches_messagepack_bin_encoding(int payloadLength)
        {
            var serializer = new MessagePackNetSerializer();
            var payload = Body(payloadLength);

            var reference = serializer.Serialize(payload);
            int headerSize = serializer.MeasureBinaryFrameHeader(payloadLength);
            var header = new byte[headerSize];
            int written = serializer.WriteBinaryFrameHeader(header, payloadLength);

            Assert.Equal(headerSize, written);
            Assert.Equal(reference.Length, headerSize + payloadLength);
            Assert.Equal(reference.Take(headerSize), header);
            Assert.Equal(reference.Skip(headerSize), payload);
        }

        [Fact]
        public void Encoded_frame_decodes_back_through_the_receive_contract()
        {
            // The receive side deserializes the wire payload as byte[] and parses the envelope from it —
            // exactly what CommandExecutor/ProtocolDispatcher do for ProtocolTypes.Envelope.
            var serializer = new MessagePackNetSerializer();
            var runtime = new SetNetRuntime().UseSerializer(serializer);
            var body = Body(1234);

            var frame = ProtocolEventFrame.Encode(runtime, channel: 555, op: 77, body);
            var envelope = serializer.Deserialize<byte[]>(frame);

            Assert.Equal(3, envelope[0]); // ProtocolKind.Event
            Assert.Equal(555, envelope[1] | (envelope[2] << 8));
            Assert.Equal(77, envelope[3] | (envelope[4] << 8));
            Assert.Equal(0, envelope[5] | envelope[6] | envelope[7] | envelope[8]);
            Assert.Equal(body, envelope.Skip(9).ToArray());
        }
    }
}
