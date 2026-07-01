using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Fragmentation
{
    /// <summary>Reserved wire type carrying one fragment of a larger message (below the state-sync range). Don't reuse.</summary>
    public static class FragmentTypes
    {
        /// <summary>A single fragment: <c>[4 msgId][2 origType][2 index][2 count][chunk]</c>.</summary>
        public const ushort Fragment = ushort.MaxValue - 18;   // 65517
    }

    /// <summary>Splits application messages that exceed a datagram into numbered fragments, and reassembles them on the far side.</summary>
    internal static class FragmentCodec
    {
        public const int HeaderSize = 10;

        public static byte[] Encode(uint msgId, ushort origType, ushort index, ushort count, byte[] payload, int offset, int length)
        {
            var frame = new byte[HeaderSize + length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), msgId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), origType);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), index);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), count);
            Buffer.BlockCopy(payload, offset, frame, HeaderSize, length);
            return frame;
        }
    }

    /// <summary>
    /// Buffers incoming fragments per message id and yields the reassembled payload once all fragments arrive. Bounded
    /// by an in-flight cap and a staleness timeout so never-completed sets (loss, or an abusive sender) can't leak memory.
    /// </summary>
    internal sealed class Reassembler
    {
        private sealed class Partial
        {
            public byte[]?[] Chunks = Array.Empty<byte[]>();
            public int Received;
            public ushort OrigType;
            public long FirstTicks;
        }

        private readonly ConcurrentDictionary<uint, Partial> _partials = new ConcurrentDictionary<uint, Partial>();
        private readonly int _maxInFlight;
        private readonly double _timeoutSeconds;

        public Reassembler(int maxInFlight = 256, double timeoutSeconds = 10)
        {
            _maxInFlight = maxInFlight;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>Adds a fragment; returns the completed (type, payload) when the last one arrives, otherwise null.</summary>
        public (ushort type, byte[] payload)? Add(byte[] frame)
        {
            if (frame.Length < FragmentCodec.HeaderSize) return null;
            var msgId = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, 4));
            var origType = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8, 2));
            if (count == 0 || index >= count) return null;

            Sweep();

            var chunk = new byte[frame.Length - FragmentCodec.HeaderSize];
            Buffer.BlockCopy(frame, FragmentCodec.HeaderSize, chunk, 0, chunk.Length);

            var partial = _partials.GetOrAdd(msgId, _ => new Partial
            {
                Chunks = new byte[count][],
                OrigType = origType,
                FirstTicks = Stopwatch.GetTimestamp(),
            });

            lock (partial)
            {
                if (index >= partial.Chunks.Length) return null;
                if (partial.Chunks[index] == null) { partial.Chunks[index] = chunk; partial.Received++; }
                if (partial.Received != partial.Chunks.Length) return null;

                _partials.TryRemove(msgId, out _);
                var total = 0;
                foreach (var c in partial.Chunks) total += c!.Length;
                var full = new byte[total];
                var pos = 0;
                foreach (var c in partial.Chunks) { Buffer.BlockCopy(c!, 0, full, pos, c!.Length); pos += c!.Length; }
                return (partial.OrigType, full);
            }
        }

        private void Sweep()
        {
            if (_partials.Count < _maxInFlight) { DropStale(); return; }
            DropStale();
            // If still over the cap, drop the oldest partial to bound memory hard.
            if (_partials.Count >= _maxInFlight)
            {
                uint oldestKey = 0; long oldest = long.MaxValue; var found = false;
                foreach (var kv in _partials) if (kv.Value.FirstTicks < oldest) { oldest = kv.Value.FirstTicks; oldestKey = kv.Key; found = true; }
                if (found) _partials.TryRemove(oldestKey, out _);
            }
        }

        private void DropStale()
        {
            var now = Stopwatch.GetTimestamp();
            List<uint>? drop = null;
            foreach (var kv in _partials)
                if ((now - kv.Value.FirstTicks) / (double)Stopwatch.Frequency > _timeoutSeconds)
                    (drop ??= new List<uint>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _partials.TryRemove(k, out _);
        }
    }

    /// <summary>
    /// Application-level fragmentation for UDP: send messages larger than a datagram and have them reassembled
    /// transparently on the far side, delivered to their normal typed handler. Only needed on UDP (TCP/WebSockets are
    /// streams and already carry any size); most useful with reliable delivery, since a lost fragment loses the message.
    /// </summary>
    public static class FragmentationExtensions
    {
        private static long _counter;
        private static readonly ConcurrentDictionary<BaseClient, byte> Clients = new ConcurrentDictionary<BaseClient, byte>();
        private static readonly Reassembler ClientReassembler = new Reassembler();

        /// <summary>Registers a client so it reassembles incoming fragmented messages. Call once after constructing the client.</summary>
        public static void UseFragmentation(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            Clients[client] = 0;
        }

        /// <summary>Sends a message from a client, splitting it into fragments if it exceeds <paramref name="maxChunk"/> bytes.</summary>
        public static Task SendFragmentedAsync(this BaseClient client, ushort type, byte[] payload, DeliveryMethod delivery, int maxChunk = 1100)
            => SendFragmented(
                sendFragment: frame => client.SendAsync(FragmentTypes.Fragment, frame, delivery),  // byte[] rides the serializer (like RPC/Rooms)
                sendWhole: () => client.SendRawAsync(type, payload ?? Array.Empty<byte>(), delivery), // whole = already-serialized original, raw
                type, payload, maxChunk);

        /// <summary>Sends a message from a server peer, splitting it into fragments if it exceeds <paramref name="maxChunk"/> bytes.</summary>
        public static Task SendFragmentedAsync(this BasePeer peer, ushort type, byte[] payload, DeliveryMethod delivery, int maxChunk = 1100)
            => SendFragmented(
                sendFragment: frame => peer.SendAsync(FragmentTypes.Fragment, frame, delivery),
                sendWhole: () => peer.SendRawAsync(type, payload ?? Array.Empty<byte>(), delivery),
                type, payload, maxChunk);

        private static async Task SendFragmented(Func<byte[], Task> sendFragment, Func<Task> sendWhole, ushort type, byte[] payload, int maxChunk)
        {
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length <= maxChunk) { await sendWhole().ConfigureAwait(false); return; }

            var msgId = unchecked((uint)Interlocked.Increment(ref _counter));
            var count = (ushort)((payload.Length + maxChunk - 1) / maxChunk);
            for (ushort i = 0; i < count; i++)
            {
                var offset = i * maxChunk;
                var len = Math.Min(maxChunk, payload.Length - offset);
                var frame = FragmentCodec.Encode(msgId, type, i, count, payload, offset, len);
                await sendFragment(frame).ConfigureAwait(false);
            }
        }

        internal static void OnClientFragment(byte[] frame)
        {
            var done = ClientReassembler.Add(frame);
            if (done == null) return;
            foreach (var client in Clients.Keys) client.InjectFrame(done.Value.type, done.Value.payload);
        }
    }

    /// <summary>Auto-discovered server handler that reassembles fragments per peer and re-injects the whole message.</summary>
    [MessageHandler(FragmentTypes.Fragment)]
    public sealed class FragmentServerHandler : IServerMessageHandler<byte[]>
    {
        private static readonly ConditionalWeakTable<BasePeer, Reassembler> PeerReassemblers = new ConditionalWeakTable<BasePeer, Reassembler>();

        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var reassembler = PeerReassemblers.GetValue(peer, _ => new Reassembler());
            var done = reassembler.Add(data);
            if (done != null) peer.InjectFrame(done.Value.type, done.Value.payload);
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler that reassembles fragments and re-injects the whole message.</summary>
    [MessageHandler(FragmentTypes.Fragment)]
    public sealed class FragmentClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            FragmentationExtensions.OnClientFragment(data);
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time fragmentation bootstrap. Call <see cref="Enable"/> at startup so the handlers are discovered.</summary>
    public static class FragmentationRuntime
    {
        /// <summary>Ensures the fragmentation layer is discoverable. Call once at startup.</summary>
        public static void Enable() { _ = FragmentTypes.Fragment; }
    }
}
