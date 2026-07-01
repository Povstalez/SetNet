using System;
using System.Buffers.Binary;

namespace SetNet.Rpc
{
    /// <summary>
    /// One decoded RPC envelope: a correlation id (to match a response to its request), the method id, an error
    /// flag, and the serialized body. The body is the request/response object serialized with the app's
    /// configured <see cref="SetNet.Messaging.SetNetSerializer"/>; only this thin envelope is fixed by RPC.
    /// </summary>
    internal readonly struct RpcFrame
    {
        /// <summary>Process-unique id linking a response back to the awaiting request.</summary>
        public readonly int CorrelationId;

        /// <summary>The RPC method id (matches an <see cref="RpcMethodAttribute"/> on the server side).</summary>
        public readonly ushort MethodId;

        /// <summary>True when <see cref="Body"/> carries a UTF-8 error message instead of a serialized response.</summary>
        public readonly bool IsError;

        /// <summary>The serialized request/response body (or, when <see cref="IsError"/>, a UTF-8 error string).</summary>
        public readonly byte[] Body;

        /// <summary>Creates a frame from its parts.</summary>
        public RpcFrame(int correlationId, ushort methodId, bool isError, byte[] body)
        {
            CorrelationId = correlationId;
            MethodId = methodId;
            IsError = isError;
            Body = body;
        }

        // Fixed little-endian header: [int correlationId][ushort methodId][byte flags], then the raw body.
        private const int HeaderSize = 4 + 2 + 1;

        /// <summary>
        /// Encodes the envelope to a single byte array. The result is sent as a <c>byte[]</c> message, which any
        /// <c>ISerializer</c> can carry without type attributes — so RPC stays serializer-agnostic.
        /// </summary>
        public byte[] Encode()
        {
            var body = Body ?? Array.Empty<byte>();
            var buffer = new byte[HeaderSize + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), CorrelationId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), MethodId);
            buffer[6] = IsError ? (byte)1 : (byte)0;
            Buffer.BlockCopy(body, 0, buffer, HeaderSize, body.Length);
            return buffer;
        }

        /// <summary>Decodes an envelope produced by <see cref="Encode"/>.</summary>
        /// <exception cref="RpcException">If the frame is too short to contain the header.</exception>
        public static RpcFrame Decode(byte[] frame)
        {
            if (frame == null || frame.Length < HeaderSize)
                throw new RpcException("Malformed RPC envelope.");

            var correlationId = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
            var methodId = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2));
            var isError = frame[6] != 0;
            var body = new byte[frame.Length - HeaderSize];
            Buffer.BlockCopy(frame, HeaderSize, body, 0, body.Length);
            return new RpcFrame(correlationId, methodId, isError, body);
        }
    }
}
