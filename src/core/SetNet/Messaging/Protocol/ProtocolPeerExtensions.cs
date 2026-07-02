using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// The uniform server-side push surface for the unified protocol. A channel service pushes events to specific
    /// peers it tracks (e.g. the members of a room) with the same two verbs everywhere, instead of each module
    /// framing its own event and calling <c>SendAsync</c> against a bespoke wire type.
    /// </summary>
    public static class ProtocolPeerExtensions
    {
        /// <summary>Pushes a raw event body to one peer for (channel, op). Serializer-agnostic.</summary>
        public static Task PublishRawAsync(this BasePeer peer, ushort channel, ushort op, byte[]? body = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var env = new ProtocolEnvelope(ProtocolKind.Event, channel, op, 0, body);
            return peer.SendAsync(ProtocolTypes.Envelope, env.Encode(), DeliveryMethod.Reliable);
        }

        /// <summary>Pushes a typed event to one peer for (channel, op): serializes <typeparamref name="T"/> via the app serializer.</summary>
        public static Task PublishAsync<T>(this BasePeer peer, ushort channel, ushort op, T evt)
            => peer.PublishRawAsync(channel, op, SetNetSerializer.Serialize(evt));

        /// <summary>Pushes one raw event body to every peer in the sequence (best-effort; a dropping peer is skipped).</summary>
        public static async Task PublishRawAsync(this IEnumerable<BasePeer> peers, ushort channel, ushort op, byte[]? body = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            // Encode once and reuse across recipients.
            var frame = new ProtocolEnvelope(ProtocolKind.Event, channel, op, 0, body).Encode();
            foreach (var peer in peers)
            {
                try { await peer.SendAsync(ProtocolTypes.Envelope, frame, DeliveryMethod.Reliable).ConfigureAwait(false); }
                catch { /* member dropping; skip */ }
            }
        }

        /// <summary>Pushes one typed event to every peer in the sequence (best-effort).</summary>
        public static Task PublishAsync<T>(this IEnumerable<BasePeer> peers, ushort channel, ushort op, T evt)
            => peers.PublishRawAsync(channel, op, SetNetSerializer.Serialize(evt));
    }
}
