using System;

namespace SetNet.Core
{
    /// <summary>
    /// Length-prefixed TCP framing: <c>[4-byte int length][2-byte ushort type][payload]</c>.
    /// Reassembly uses a growable byte buffer with read/write offsets — packets that span
    /// multiple socket reads are reassembled correctly, and draining is amortized O(1)
    /// (no full-buffer copy per packet).
    /// </summary>
    public class PacketBuilder
    {
        /// <summary>
        /// Backing reassembly buffer. Grows on demand (doubling) and is compacted toward the front
        /// when it runs out of room; never shrinks. Holds the bytes between <see cref="_start"/> and
        /// <see cref="_end"/> that have been received but not yet drained as complete packets.
        /// </summary>
        private byte[] _buffer = new byte[4096];

        /// <summary>Read offset: index of the first byte not yet consumed by <see cref="TryGetCompletePacket"/>.</summary>
        private int _start; // read offset

        /// <summary>Write offset: index one past the last buffered byte (buffered count = <c>_end - _start</c>).</summary>
        private int _end;   // write offset (buffered count = _end - _start)

        /// <summary>Bytes of framing overhead before the payload (4-byte length + 2-byte type).</summary>
        public const int HeaderSize = 6;

        /// <summary>Maximum accepted frame length (0 = unlimited); a larger declared length is rejected.</summary>
        private readonly int _maxFrameSize;

        /// <summary>Creates a reassembler with no frame-size cap.</summary>
        public PacketBuilder() : this(0) { }

        /// <summary>Creates a reassembler that rejects frames whose declared length exceeds the cap.</summary>
        /// <param name="maxFrameSize">Maximum allowed frame length in bytes; 0 disables the cap.</param>
        public PacketBuilder(int maxFrameSize)
        {
            _maxFrameSize = maxFrameSize;
        }

        /// <summary>
        /// Builds a fully framed, self-contained packet (<c>[4-byte length][2-byte type][payload]</c>) for a
        /// single message. Convenience wrapper over <see cref="WriteFrame(byte[], ushort, byte[], int)"/> that allocates an exactly-sized
        /// array; use it when you do not have a pooled buffer to frame into.
        /// </summary>
        /// <param name="type">The message type identifier written into the 2-byte type field of the frame.</param>
        /// <param name="data">The serialized payload to wrap; copied verbatim after the header.</param>
        /// <returns>A newly allocated byte array containing the complete framed packet, ready to write to the socket.</returns>
        public byte[] BuildPacket(ushort type, byte[] data)
        {
            var packet = new byte[HeaderSize + data.Length];
            WriteFrame(packet, type, data, data.Length);
            return packet;
        }

        /// <summary>
        /// Writes <c>[4-byte length][2-byte type][payload]</c> into <paramref name="dest"/> (which may be a
        /// pooled, oversized buffer) and returns the total framed length. Lets the send path frame into a
        /// rented buffer with no per-send allocation.
        /// </summary>
        /// <param name="dest">
        /// Destination buffer to write the frame into. Must have room for at least
        /// <see cref="HeaderSize"/> + <paramref name="payloadLength"/> bytes; may be larger (e.g. a pooled buffer).
        /// </param>
        /// <param name="type">The message type identifier written into the 2-byte type field.</param>
        /// <param name="payload">The serialized payload bytes to copy after the header.</param>
        /// <param name="payloadLength">
        /// Number of bytes to copy from the start of <paramref name="payload"/>. Allows framing only the
        /// valid prefix of an oversized/pooled payload array.
        /// </param>
        /// <returns>The total number of bytes written to <paramref name="dest"/> (header plus payload).</returns>
        /// <remarks>
        /// The 4-byte length prefix is little-endian and counts the type field plus the payload (it does
        /// <em>not</em> include the 4 length bytes themselves), so the reassembler reads <c>length</c> bytes after
        /// the prefix. The 2-byte type is also little-endian.
        /// </remarks>
        public static int WriteFrame(byte[] dest, ushort type, byte[] payload, int payloadLength)
            => WriteFrame(dest, 0, type, payload, payloadLength);

        /// <summary>
        /// Writes a framed packet into <paramref name="dest"/> starting at <paramref name="destOffset"/> and returns
        /// the total framed length. Used by the batched send path to append several frames into one buffer.
        /// </summary>
        /// <param name="dest">Destination buffer; must have room for <see cref="HeaderSize"/> + <paramref name="payloadLength"/> bytes at <paramref name="destOffset"/>.</param>
        /// <param name="destOffset">Index in <paramref name="dest"/> at which to start writing the frame.</param>
        /// <param name="type">The message type identifier (2-byte little-endian).</param>
        /// <param name="payload">The serialized payload to copy after the header.</param>
        /// <param name="payloadLength">Number of payload bytes to copy.</param>
        /// <returns>The total number of bytes written (header plus payload).</returns>
        public static int WriteFrame(byte[] dest, int destOffset, ushort type, byte[] payload, int payloadLength)
        {
            var length = payloadLength + 2; // payload + 2-byte type
            dest[destOffset] = (byte)length;
            dest[destOffset + 1] = (byte)(length >> 8);
            dest[destOffset + 2] = (byte)(length >> 16);
            dest[destOffset + 3] = (byte)(length >> 24);
            dest[destOffset + 4] = (byte)type;
            dest[destOffset + 5] = (byte)(type >> 8);
            Buffer.BlockCopy(payload, 0, dest, destOffset + HeaderSize, payloadLength);
            return HeaderSize + payloadLength;
        }

        /// <summary>
        /// Splits an already de-framed packet (the bytes <see cref="TryGetCompletePacket"/> returns, with the
        /// length prefix stripped) into its message type and payload. This is the inverse of the type/payload
        /// portion of <see cref="WriteFrame(byte[], ushort, byte[], int)"/> and is how the receive path recovers a typed message to dispatch.
        /// </summary>
        /// <param name="packet">A de-framed packet laid out as <c>[2-byte type][payload]</c>.</param>
        /// <returns>
        /// A tuple of the decoded little-endian message <c>type</c> and a freshly allocated array holding the
        /// payload bytes that follow it.
        /// </returns>
        public static (ushort, byte[]) ParsePacket(byte[] packet)
        {
            // packet = [2-byte type][payload] (length prefix already stripped by the reassembler).
            // A well-formed packet always carries at least the 2-byte type; anything shorter is a
            // corrupt/malicious frame — return an empty message rather than indexing past the end.
            if (packet.Length < 2)
                return (0, Array.Empty<byte>());
            var type = (ushort)(packet[0] | (packet[1] << 8));
            var data = new byte[packet.Length - 2];
            Buffer.BlockCopy(packet, 2, data, 0, data.Length);
            return (type, data);
        }

        /// <summary>
        /// Feeds a whole array of freshly received bytes into the reassembly buffer. Convenience overload that
        /// appends the entire <paramref name="data"/> array.
        /// </summary>
        /// <param name="data">The bytes just read from the socket to append to the pending buffer.</param>
        public void AppendData(byte[] data) => AppendData(data, 0, data.Length);

        /// <summary>Append a slice of <paramref name="data"/> without first allocating a sub-array copy.</summary>
        /// <param name="data">The buffer (typically the socket read buffer) containing the received bytes.</param>
        /// <param name="offset">The index in <paramref name="data"/> at which the received bytes begin.</param>
        /// <param name="count">The number of bytes to append starting at <paramref name="offset"/>.</param>
        /// <remarks>
        /// May grow and/or compact the internal buffer to make room (see <c>EnsureCapacity</c>). Not thread-safe;
        /// callers must serialize appends with calls to <see cref="TryGetCompletePacket"/>.
        /// </remarks>
        public void AppendData(byte[] data, int offset, int count)
        {
            EnsureCapacity(count);
            Buffer.BlockCopy(data, offset, _buffer, _end, count);
            _end += count;
        }

        /// <summary>
        /// Attempts to extract the next fully buffered, de-framed packet. Drains exactly one frame per call so
        /// the receive loop can pump this in a <c>while</c> loop until it returns <see langword="false"/>, which
        /// is what lets a single socket read yield zero, one, or many messages and lets one message span reads.
        /// </summary>
        /// <param name="packet">
        /// When the method returns <see langword="true"/>, receives a newly allocated array containing the
        /// packet with its 4-byte length prefix removed (i.e. <c>[2-byte type][payload]</c>, ready for
        /// <see cref="ParsePacket"/>). Set to <see langword="null"/> when the method returns <see langword="false"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if a complete packet was available and has been drained; <see langword="false"/>
        /// if more bytes are still needed to complete the next frame.
        /// </returns>
        /// <remarks>
        /// A negative decoded length prefix is treated as a corrupt or malicious stream: the buffer is reset and
        /// the method returns <see langword="false"/> rather than throwing. Not thread-safe; serialize with
        /// <see cref="AppendData(byte[],int,int)"/>.
        /// </remarks>
        public bool TryGetCompletePacket(out byte[] packet)
        {
            packet = null!;

            var available = _end - _start;
            if (available < 4)
                return false;

            var length = BitConverter.ToInt32(_buffer, _start);

            // Guard against a corrupt/malicious length prefix. A valid frame's length counts the
            // 2-byte type plus payload, so it must be >= 2; anything smaller (negative, 0, 1) is
            // corrupt — reset and resync rather than buffering a bogus body.
            if (length < 2)
            {
                _start = _end = 0;
                return false;
            }

            // Reject oversized frames before buffering their body (slow-loris / OOM protection).
            if (_maxFrameSize > 0 && length > _maxFrameSize)
                throw new System.IO.InvalidDataException(
                    $"Incoming frame length {length} exceeds the configured maximum of {_maxFrameSize} bytes.");

            if (available < length + 4)
                return false;

            packet = new byte[length];
            Buffer.BlockCopy(_buffer, _start + 4, packet, 0, length);
            _start += length + 4;

            // Reset offsets once fully drained so the buffer doesn't creep forward.
            if (_start == _end)
                _start = _end = 0;

            return true;
        }

        /// <summary>
        /// Like <see cref="TryGetCompletePacket"/> but decodes the next frame directly into its message type and
        /// payload, copying only the payload once (instead of copying the whole frame and then the payload again).
        /// This is the allocation-light receive path used by the TCP transport.
        /// </summary>
        /// <param name="type">When the method returns <see langword="true"/>, the decoded little-endian message type.</param>
        /// <param name="payload">
        /// When the method returns <see langword="true"/>, a newly allocated array holding just the payload bytes
        /// (no length prefix, no type field). Set to <see langword="null"/> when the method returns <see langword="false"/>.
        /// </param>
        /// <returns><see langword="true"/> if a complete frame was drained; otherwise <see langword="false"/>.</returns>
        /// <exception cref="System.IO.InvalidDataException">The declared frame length exceeds the configured maximum.</exception>
        public bool TryGetCompleteMessage(out ushort type, out byte[] payload)
        {
            type = 0;
            payload = null!;

            var available = _end - _start;
            if (available < 4)
                return false;

            var length = BitConverter.ToInt32(_buffer, _start);
            // length counts the 2-byte type + payload, so a valid frame is always >= 2.
            // Negative/0/1 is a corrupt or malicious prefix: reset and resync rather than
            // computing a negative payload length (which would throw on allocation).
            if (length < 2)
            {
                _start = _end = 0;
                return false;
            }
            if (_maxFrameSize > 0 && length > _maxFrameSize)
                throw new System.IO.InvalidDataException(
                    $"Incoming frame length {length} exceeds the configured maximum of {_maxFrameSize} bytes.");
            if (available < length + 4)
                return false;

            // Frame layout: [4-byte length][2-byte type][payload]; length counts type + payload.
            type = (ushort)(_buffer[_start + 4] | (_buffer[_start + 5] << 8));
            var payloadLength = length - 2;
            payload = new byte[payloadLength];
            Buffer.BlockCopy(_buffer, _start + 6, payload, 0, payloadLength);
            _start += length + 4;

            if (_start == _end)
                _start = _end = 0;

            return true;
        }

        /// <summary>
        /// Guarantees the backing buffer can accept <paramref name="extra"/> more bytes at <see cref="_end"/>.
        /// First reclaims space by compacting unread data to the front (cheap), and only if that is still
        /// insufficient does it allocate a larger buffer (doubling until it fits) and copy the live bytes over.
        /// Keeps appends amortized O(1) and avoids unbounded forward creep of the read window.
        /// </summary>
        /// <param name="extra">The number of additional bytes about to be written at the write offset.</param>
        private void EnsureCapacity(int extra)
        {
            if (_end + extra <= _buffer.Length)
                return;

            // Compact unread data to the front first.
            var count = _end - _start;
            if (_start > 0)
            {
                if (count > 0)
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, count);
                _start = 0;
                _end = count;
            }

            if (_end + extra <= _buffer.Length)
                return;

            var newSize = _buffer.Length * 2;
            while (newSize < _end + extra)
                newSize *= 2;

            var grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _end);
            _buffer = grown;
        }
    }
}
