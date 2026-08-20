using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;

namespace SetNet.Protocol
{
    /// <summary>
    /// The uniform server-side push surface for the unified protocol. A channel service pushes events to specific
    /// peers it tracks (e.g. the members of a room) with the same two verbs everywhere, instead of each module
    /// framing its own event and calling <c>SendAsync</c> against a bespoke wire type.
    /// </summary>
    public static class ProtocolPeerExtensions
    {
        /// <summary>
        /// Pushes a raw event body to one peer for (channel, op). Serializer-agnostic. The complete wire
        /// payload is built in one buffer by <see cref="ProtocolEventFrame.Encode"/> — the legacy path encoded
        /// the envelope into its own array and then re-copied it inside <c>SendAsync&lt;byte[]&gt;</c> on every
        /// publish. Wire bytes are unchanged, so mixed-version peers interoperate.
        /// </summary>
        public static Task PublishRawAsync(this BasePeer peer, ushort channel, ushort op, byte[]? body = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var frame = ProtocolEventFrame.Encode(peer.Runtime, channel, op, body);
            return peer.SendRawAsync(ProtocolTypes.Envelope, frame, DeliveryMethod.Reliable);
        }

        /// <summary>Pushes a typed event to one peer for (channel, op): serializes <typeparamref name="T"/> via the app serializer.</summary>
        public static Task PublishAsync<T>(this BasePeer peer, ushort channel, ushort op, T evt)
            => peer.PublishRawAsync(channel, op, peer.Runtime.Serialize(evt));

        /// <summary>
        /// Sends an already-encoded event frame (see <see cref="ProtocolEventFrame.Encode"/>) to one peer.
        /// This is the zero-extra-allocation fan-out primitive: encode the frame once for a whole audience,
        /// then push the same array to every recipient — the per-recipient cost above the transport is zero.
        /// </summary>
        /// <param name="peer">The recipient.</param>
        /// <param name="eventFrame">The complete wire payload built by <see cref="ProtocolEventFrame.Encode"/>.</param>
        /// <param name="delivery">The delivery guarantee for this send.</param>
        public static Task PublishFrameAsync(
            this BasePeer peer, byte[] eventFrame, DeliveryMethod delivery = DeliveryMethod.Reliable)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (eventFrame == null) throw new ArgumentNullException(nameof(eventFrame));
            return peer.SendRawAsync(ProtocolTypes.Envelope, eventFrame, delivery);
        }

        /// <summary>
        /// Pushes one raw event body to every peer in the sequence (best-effort; a dropping peer is skipped).
        /// The wire frame is encoded once per distinct runtime — for the usual one-runtime process that is
        /// exactly once for the whole audience, with no per-recipient re-wrapping.
        /// </summary>
        public static async Task PublishRawAsync(this IEnumerable<BasePeer> peers, ushort channel, ushort op, byte[]? body = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));

            // Peers of one server share a runtime, so this normally encodes once. Isolated-runtime peers in
            // the same sequence (rare, but allowed) each get a frame built with THEIR serializer, because the
            // wrap header is a serializer detail and mixing them would corrupt the frame for that peer.
            SetNetRuntime? encodedFor = null;
            byte[]? frame = null;
            Dictionary<SetNetRuntime, byte[]>? perRuntime = null;

            foreach (var peer in peers)
            {
                var runtime = peer.Runtime;
                byte[] send;
                if (frame != null && ReferenceEquals(encodedFor, runtime))
                    send = frame;
                else if (perRuntime != null && perRuntime.TryGetValue(runtime, out var cached))
                    send = cached;
                else
                {
                    send = ProtocolEventFrame.Encode(runtime, channel, op, body);
                    if (frame == null)
                    {
                        frame = send;
                        encodedFor = runtime;
                    }
                    else
                    {
                        perRuntime ??= new Dictionary<SetNetRuntime, byte[]>();
                        perRuntime[runtime] = send;
                    }
                }

                try { await peer.SendRawAsync(ProtocolTypes.Envelope, send, DeliveryMethod.Reliable).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext); }
                catch { /* member dropping; skip */ }
            }
        }

        /// <summary>Pushes one typed event to every peer in the sequence (best-effort).</summary>
        public static Task PublishAsync<T>(this IEnumerable<BasePeer> peers, ushort channel, ushort op, T evt)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            return PublishTypedAsync(peers, channel, op, evt);
        }

        private static async Task PublishTypedAsync<T>(IEnumerable<BasePeer> peers, ushort channel, ushort op, T evt)
        {
            foreach (var peer in peers)
            {
                try { await peer.PublishAsync(channel, op, evt).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext); }
                catch { }
            }
        }
    }
}
