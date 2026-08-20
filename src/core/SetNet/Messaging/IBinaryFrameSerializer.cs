using System;

namespace SetNet.Messaging
{
    /// <summary>
    /// Optional <see cref="ISerializer"/> capability: exposes how the serializer frames a raw <c>byte[]</c>
    /// payload, so callers can build the serialized representation of a binary payload directly into one
    /// buffer instead of materializing the payload first and re-copying it through
    /// <see cref="ISerializer.Serialize{T}"/>.
    /// </summary>
    /// <remarks>
    /// The motivating hot path is the unified protocol's push-event publish: the envelope bytes used to be
    /// encoded into their own array and then wrapped by <c>Serialize&lt;byte[]&gt;</c> into a second, full copy —
    /// one extra allocation and one extra copy per event, per recipient. With this capability the publisher
    /// writes <c>[wrap header][payload]</c> once.
    /// <para>
    /// Contract: for any payload <c>p</c>, <c>Serialize&lt;byte[]&gt;(p)</c> MUST equal the concatenation of the
    /// header written by <see cref="WriteBinaryFrameHeader"/> for <c>p.Length</c> followed by the raw bytes of
    /// <c>p</c>. Implementations whose <c>byte[]</c> encoding is not a plain header-plus-bytes framing (for
    /// example base64-in-JSON) must NOT implement this interface; callers fall back to
    /// <see cref="ISerializer.Serialize{T}"/> automatically.
    /// </para>
    /// </remarks>
    public interface IBinaryFrameSerializer
    {
        /// <summary>Returns the size in bytes of the wrap header for a binary payload of the given length.</summary>
        /// <param name="payloadLength">The raw payload length in bytes.</param>
        /// <returns>The number of header bytes that precede the raw payload in the serialized form.</returns>
        int MeasureBinaryFrameHeader(int payloadLength);

        /// <summary>
        /// Writes the wrap header for a binary payload of the given length into
        /// <paramref name="destination"/> (which must be at least
        /// <see cref="MeasureBinaryFrameHeader"/> bytes long).
        /// </summary>
        /// <param name="destination">The buffer that receives the header at offset 0.</param>
        /// <param name="payloadLength">The raw payload length in bytes.</param>
        /// <returns>The number of header bytes written.</returns>
        int WriteBinaryFrameHeader(Span<byte> destination, int payloadLength);
    }
}
