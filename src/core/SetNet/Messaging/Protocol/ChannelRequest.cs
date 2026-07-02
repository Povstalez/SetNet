using System;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// The server-side context for one inbound protocol message: which <see cref="Peer"/> sent it, the
    /// <see cref="Channel"/>/<see cref="Op"/>, and the body — plus the reply helpers. A channel service reads the
    /// request and, when the caller is awaiting one, sends exactly one reply (raw or typed). All correlation and
    /// framing is handled here, so the service only deals in payloads.
    /// </summary>
    public sealed class ChannelRequest
    {
        /// <summary>The peer that sent this message.</summary>
        public BasePeer Peer { get; }

        /// <summary>The channel id this message targets.</summary>
        public ushort Channel { get; }

        /// <summary>The operation id within the channel (module-assigned).</summary>
        public ushort Op { get; }

        /// <summary>The raw, opaque request body (never null; empty when there was no body).</summary>
        public byte[] RawBody { get; }

        // Non-zero for a Request that expects a reply; 0 for a fire-and-forget Send.
        private readonly int _corr;
        private int _replied;

        /// <summary>True when the caller is awaiting a reply (i.e. this was a request, not a fire-and-forget send).</summary>
        public bool ExpectsReply => _corr != 0;

        internal ChannelRequest(BasePeer peer, ushort channel, ushort op, int corr, byte[] rawBody)
        {
            Peer = peer;
            Channel = channel;
            Op = op;
            _corr = corr;
            RawBody = rawBody ?? Array.Empty<byte>();
        }

        /// <summary>Deserializes the body into <typeparamref name="T"/> via the app's <see cref="SetNetSerializer"/>.</summary>
        public T Read<T>() => SetNetSerializer.Deserialize<T>(RawBody);

        /// <summary>Sends a raw (already-framed) reply body back to the caller. Serializer-agnostic. Call at most once.</summary>
        public Task ReplyRawAsync(byte[] body)
        {
            if (System.Threading.Interlocked.Exchange(ref _replied, 1) != 0) return Task.CompletedTask;
            var env = new ProtocolEnvelope(ProtocolKind.Reply, Channel, Op, _corr, body);
            return Peer.SendAsync(ProtocolTypes.Envelope, env.Encode(), DeliveryMethod.Reliable);
        }

        /// <summary>Serializes <paramref name="response"/> via the app's serializer and sends it as the reply. Call at most once.</summary>
        public Task ReplyAsync<T>(T response) => ReplyRawAsync(SetNetSerializer.Serialize(response));

        /// <summary>Sends an error reply the caller re-throws as a <see cref="ProtocolException"/>. Used by the dispatcher; also callable by a service.</summary>
        public Task ReplyErrorAsync(string message)
        {
            if (System.Threading.Interlocked.Exchange(ref _replied, 1) != 0) return Task.CompletedTask;
            var env = new ProtocolEnvelope(ProtocolKind.Error, Channel, Op, _corr,
                System.Text.Encoding.UTF8.GetBytes(message ?? ""));
            return Peer.SendAsync(ProtocolTypes.Envelope, env.Encode(), DeliveryMethod.Reliable);
        }
    }
}
