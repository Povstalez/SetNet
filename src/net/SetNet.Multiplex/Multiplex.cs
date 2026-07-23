using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace SetNet.Multiplex
{
    /// <summary>Reserved wire type carrying one multiplexed frame. Don't reuse this id for application messages.</summary>
    public static class MuxTypes
    {
        /// <summary>A multiplexed frame: <c>[1 channel][2 origType LE][payload]</c>.</summary>
        public const ushort Frame = ushort.MaxValue - 41;   // 65494
    }

    /// <summary>Encodes/decodes the tiny mux envelope in front of the original frame.</summary>
    internal static class MuxCodec
    {
        public const int HeaderSize = 3;

        public static byte[] Encode(byte channel, ushort origType, byte[] payload)
        {
            var frame = new byte[HeaderSize + payload.Length];
            frame[0] = channel;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), origType);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return frame;
        }

        public static (byte Channel, ushort OrigType, byte[] Payload)? Decode(byte[] frame)
        {
            if (frame == null || frame.Length < HeaderSize) return null;
            var channel = frame[0];
            var origType = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
            var payload = new byte[frame.Length - HeaderSize];
            Buffer.BlockCopy(frame, HeaderSize, payload, 0, payload.Length);
            return (channel, origType, payload);
        }
    }

    /// <summary>
    /// Per-socket demultiplexer: one FIFO lane per channel id. Each lane drains on its own task, so a slow
    /// (or bursty) channel never delays delivery on the others — while frames <b>within</b> a channel are always
    /// injected in arrival order. Injection goes through <see cref="BaseSocket.InjectFrame"/>, i.e. the original
    /// typed handler runs exactly as if the frame had arrived unwrapped.
    /// </summary>
    internal sealed class MuxDemux
    {
        // Per-lane backlog cap. Injected frames bypass the core inbound-queue back-pressure (they're already
        // "handled" from the socket's view), so an unbounded lane would let a flooder grow memory freely; past this
        // depth Enqueue refuses the frame and the caller drops the peer.
        private const int MaxLaneQueueDepth = 4096;

        private sealed class Lane
        {
            public readonly ConcurrentQueue<(ushort Type, byte[] Payload)> Queue = new ConcurrentQueue<(ushort, byte[])>();
            public int Draining;   // 0 = idle, 1 = a drain task owns the queue
            public int Depth;      // queued items not yet drained (tracked so the cap check is O(1))
        }

        private readonly ConcurrentDictionary<byte, Lane> _lanes = new ConcurrentDictionary<byte, Lane>();
        private readonly Action<ushort, byte[]> _inject;

        public MuxDemux(Action<ushort, byte[]> inject) => _inject = inject;

        /// <summary>Queues a decoded frame on its channel's lane and ensures a drain task is running for it. Returns false when the lane is over its backlog cap (the frame is dropped).</summary>
        public bool Enqueue(byte channel, ushort origType, byte[] payload)
        {
            var lane = _lanes.GetOrAdd(channel, _ => new Lane());
            if (Volatile.Read(ref lane.Depth) >= MaxLaneQueueDepth) return false;   // lane flooded faster than it drains
            Interlocked.Increment(ref lane.Depth);
            lane.Queue.Enqueue((origType, payload));
            ScheduleDrain(lane);
            return true;
        }

        private void ScheduleDrain(Lane lane)
        {
            // Only one drain task per lane at a time; the loop re-checks after releasing ownership so an item
            // enqueued during the hand-off window is never stranded.
            if (Interlocked.CompareExchange(ref lane.Draining, 1, 0) != 0) return;
            _ = Task.Run(() =>
            {
                do
                {
                    while (lane.Queue.TryDequeue(out var item))
                    {
                        Interlocked.Decrement(ref lane.Depth);
                        try { _inject(item.Type, item.Payload); } catch { /* handler faults are the socket's concern */ }
                    }
                    Interlocked.Exchange(ref lane.Draining, 0);
                } while (!lane.Queue.IsEmpty && Interlocked.CompareExchange(ref lane.Draining, 1, 0) == 0);
            });
        }
    }

    /// <summary>
    /// Logical channels over one SetNet connection. TCP (and the reliable UDP channel) deliver everything in one
    /// global order, so one heavy flow — a big state dump, a file chunk — delays every message queued behind it
    /// (head-of-line blocking at dispatch). Wrapping sends in <c>SendMuxAsync(channel, ...)</c> gives each channel
    /// its own ordered dispatch lane on the receiving side: ordering is preserved <b>within</b> a channel and
    /// independent <b>across</b> channels. The original typed handlers fire unchanged.
    /// </summary>
    public static class MultiplexExtensions
    {
        private static readonly ConditionalWeakTable<BaseClient, MuxDemux> ClientDemux = new ConditionalWeakTable<BaseClient, MuxDemux>();
        private static readonly ConcurrentDictionary<BaseClient, byte> Clients = new ConcurrentDictionary<BaseClient, byte>();

        /// <summary>Registers a client so incoming multiplexed frames are demuxed into per-channel lanes. Call once after constructing the client.</summary>
        public static void UseMultiplex(this BaseClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            Clients[client] = 0;
        }

        /// <summary>Sends already-serialized bytes on a logical channel from a client.</summary>
        public static Task SendMuxAsync(this BaseClient client, byte channel, ushort type, byte[] payload, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => client.SendAsync(MuxTypes.Frame, MuxCodec.Encode(channel, type, payload ?? Array.Empty<byte>()), delivery);   // byte[] rides the serializer (like RPC/Rooms)

        /// <summary>Sends already-serialized bytes on a logical channel from a server peer.</summary>
        public static Task SendMuxAsync(this BasePeer peer, byte channel, ushort type, byte[] payload, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => peer.SendAsync(MuxTypes.Frame, MuxCodec.Encode(channel, type, payload ?? Array.Empty<byte>()), delivery);

        /// <summary>Serializes a typed message and sends it on a logical channel; it's delivered to the normal <c>IServerMessageHandler&lt;T&gt;</c> for <paramref name="type"/>.</summary>
        public static Task SendMuxAsync<T>(this BaseClient client, byte channel, ushort type, T message, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => client.SendMuxAsync(channel, type, SetNetSerializer.Serialize(message), delivery);

        /// <summary>Serializes a typed message and sends it on a logical channel; it's delivered to the normal <c>IClientMessageHandler&lt;T&gt;</c> for <paramref name="type"/>.</summary>
        public static Task SendMuxAsync<T>(this BasePeer peer, byte channel, ushort type, T message, DeliveryMethod delivery = DeliveryMethod.Reliable)
            => peer.SendMuxAsync(channel, type, SetNetSerializer.Serialize(message), delivery);

        internal static void OnClientFrame(byte[] frame)
        {
            var decoded = MuxCodec.Decode(frame);
            if (decoded == null) return;
            // Same co-location semantics as Fragmentation: frames go to every registered client in the process
            // (one-client-per-process is the typical shape).
            foreach (var client in Clients.Keys)
            {
                var demux = ClientDemux.GetValue(client, c => new MuxDemux(c.InjectFrame));
                demux.Enqueue(decoded.Value.Channel, decoded.Value.OrigType, decoded.Value.Payload);   // over-cap frames are dropped
            }
        }
    }

    /// <summary>Auto-discovered server handler that demuxes incoming multiplexed frames per peer.</summary>
    [MessageHandler(MuxTypes.Frame)]
    public sealed class MuxServerHandler : IServerMessageHandler<byte[]>
    {
        private static readonly ConditionalWeakTable<BasePeer, MuxDemux> PeerDemux = new ConditionalWeakTable<BasePeer, MuxDemux>();

        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var decoded = MuxCodec.Decode(data);
            if (decoded != null)
            {
                var demux = PeerDemux.GetValue(peer, p => new MuxDemux(p.InjectFrame));
                // A peer that outruns its own lane drain is flooding — drop it rather than buffer unboundedly.
                if (!demux.Enqueue(decoded.Value.Channel, decoded.Value.OrigType, decoded.Value.Payload))
                    peer.CurrentPeerInfo.Disconnect();
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler that demuxes incoming multiplexed frames.</summary>
    [MessageHandler(MuxTypes.Frame)]
    public sealed class MuxClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            MultiplexExtensions.OnClientFrame(data);
            return Task.CompletedTask;
        }
    }

    /// <summary>One-time multiplex bootstrap. Call <see cref="Enable"/> at startup so the handlers are discovered.</summary>
    public static class MultiplexRuntime
    {
        /// <summary>Ensures the multiplex layer is discoverable. Call once at startup.</summary>
        public static void Enable() { _ = MuxTypes.Frame; }
    }
}
