using System;
using System.IO;
using System.IO.Compression;
using SetNet.Messaging;

namespace SetNet.Compression
{
    /// <summary>
    /// A <see cref="ISerializer"/> **decorator** that transparently Brotli-compresses another serializer's output. Wrap
    /// any inner serializer (MessagePack, JSON, …) and register the wrapper — the send path compresses, the receive path
    /// decompresses, handlers are untouched. A one-byte header flags whether a given payload was actually compressed, so
    /// small messages (below <c>minBytes</c>) and incompressible data are sent raw with no size penalty. Both ends must
    /// use the same wrapper over the same inner serializer.
    /// </summary>
    public sealed class CompressingSerializer : ISerializer
    {
        private const byte Raw = 0;
        private const byte Brotli = 1;

        private readonly ISerializer _inner;
        private readonly int _minBytes;
        private readonly CompressionLevel _level;
        private readonly long _maxDecompressedBytes;

        /// <summary>Wraps <paramref name="inner"/>. Payloads smaller than <paramref name="minBytes"/> skip compression.</summary>
        /// <param name="inner">The serializer whose output is compressed.</param>
        /// <param name="minBytes">Payloads below this size are sent uncompressed.</param>
        /// <param name="level">Brotli compression level.</param>
        /// <param name="maxDecompressedBytes">
        /// Hard ceiling on the size a single message may decompress to (default 64 MB; 0 = unbounded). A small crafted
        /// payload can expand enormously (a "decompression bomb"); decompression past this limit throws instead of
        /// allocating without bound. Keep it at or below your peers' expected largest message.
        /// </param>
        public CompressingSerializer(ISerializer inner, int minBytes = 256, CompressionLevel level = CompressionLevel.Fastest, long maxDecompressedBytes = 64L * 1024 * 1024)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _minBytes = minBytes;
            _level = level;
            _maxDecompressedBytes = maxDecompressedBytes;
        }

        /// <inheritdoc/>
        public byte[] Serialize<T>(T value)
        {
            var raw = _inner.Serialize(value);
            if (raw.Length < _minBytes) return Framed(Raw, raw);

            using var ms = new MemoryStream();
            ms.WriteByte(Brotli);
            using (var brotli = new BrotliStream(ms, _level, leaveOpen: true))
                brotli.Write(raw, 0, raw.Length);
            var compressed = ms.ToArray();

            // If compression didn't help (already-compact/incompressible), fall back to raw so we never inflate.
            return compressed.Length < raw.Length + 1 ? compressed : Framed(Raw, raw);
        }

        /// <inheritdoc/>
        public T Deserialize<T>(byte[] data)
        {
            if (data.Length == 0) return _inner.Deserialize<T>(data);
            var flag = data[0];
            if (flag == Raw)
            {
                var raw = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, raw, 0, raw.Length);
                return _inner.Deserialize<T>(raw);
            }

            using var input = new MemoryStream(data, 1, data.Length - 1);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            CopyBounded(brotli, output, _maxDecompressedBytes);
            return _inner.Deserialize<T>(output.ToArray());
        }

        /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/>, throwing once more than <paramref name="maxBytes"/> bytes have been read (0 = unbounded), so a decompression bomb can't exhaust memory.</summary>
        private static void CopyBounded(Stream source, Stream destination, long maxBytes)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (maxBytes > 0 && total > maxBytes)
                    throw new InvalidOperationException($"Decompressed payload exceeds the {maxBytes}-byte limit (possible decompression bomb).");
                destination.Write(buffer, 0, read);
            }
        }

        private static byte[] Framed(byte flag, byte[] payload)
        {
            var framed = new byte[payload.Length + 1];
            framed[0] = flag;
            Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
            return framed;
        }
    }
}
