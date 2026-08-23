using System;
using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Protocol
{
    /// <summary>
    /// The two dispatch entry points for the unified protocol, called by the reserved-envelope message handlers.
    /// The server side decodes an inbound request/send and routes it to the channel's <see cref="IChannelService"/>;
    /// the client side completes an awaiting request (reply/error) or fans a push event out to subscribers.
    /// </summary>
    internal static class ProtocolDispatcher
    {
        /// <summary>Handles an inbound envelope on the server: dispatch a request/send to its channel service.</summary>
        public static async Task DispatchServerAsync(BasePeer peer, byte[] data)
        {
            var env = ProtocolEnvelope.Decode(data);
            if (env.Kind != ProtocolKind.Request && env.Kind != ProtocolKind.Send)
                return;   // replies/events/errors are client-bound; ignore if they arrive here

            var ctx = new ChannelRequest(peer, env.Channel, env.Op, env.Corr, env.Body);
            var service = ChannelServiceRegistry.Get(env.Channel);
            if (service == null)
            {
                if (ctx.ExpectsReply)
                    await ctx.ReplyErrorAsync($"No protocol channel {env.Channel} is configured on this server.").ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);
                return;
            }

            try
            {
                await service.HandleAsync(ctx).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);
            }
            catch (Exception ex)
            {
                // Relay the failure to a waiting caller instead of dropping the request.
                if (ctx.ExpectsReply)
                    await ctx.ReplyErrorAsync(ex.Message).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);
            }
        }

        /// <summary>Handles an inbound envelope on the client: complete an awaiting request, or dispatch a push event.</summary>
        public static Task DispatchClientAsync(byte[] data)
            => DispatchClientAsync(SetNetRuntime.Default, data);

        /// <summary>Handles an inbound envelope on a scoped runtime.</summary>
        public static Task DispatchClientAsync(SetNetRuntime runtime, byte[] data)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            // Read the header first, and decode fully only when the body must outlive this call. A reply is handed
            // to a waiting caller, so it needs a body of its own; a push event is consumed by its subscribers
            // before this returns, so it can be read straight out of the received frame. That is one array saved
            // per event — the difference between noise and a steady stream of garbage in a game client taking
            // hundreds of events per frame.
            ProtocolEnvelope.DecodeHeader(data, out var kind, out var channel, out var op, out var corr);
            switch (kind)
            {
                case ProtocolKind.Reply:
                case ProtocolKind.Error:
                    ProtocolCorrelation.Complete(corr, ProtocolEnvelope.Decode(data));
                    break;
                case ProtocolKind.Event:
                    runtime.ProtocolSubscriptions.Dispatch(channel, op, ProtocolEnvelope.BodyOf(data));
                    break;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles an inbound envelope that is still sitting inside the received frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Same routing as the array overload, but the caller keeps ownership of the buffer and no copy is made for
        /// the event path. The saving is one array per delivered event: the transport already holds the bytes, and
        /// the array overload used to reach this point only after the serializer had unwrapped them into a second
        /// array of identical content.
        /// </para>
        /// <para>
        /// <b>The window must stay valid for the duration of this call, and no longer.</b> Push events are consumed
        /// by their subscribers before this returns, so a window is safe for them. Replies and errors are handed to
        /// a caller waiting on another thread and therefore still get an array of their own — that copy is not an
        /// oversight, it is the ownership boundary.
        /// </para>
        /// </remarks>
        public static Task DispatchClientAsync(SetNetRuntime runtime, ReadOnlyMemory<byte> data)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            ProtocolEnvelope.DecodeHeader(data.Span, out var kind, out var channel, out var op, out var corr);
            switch (kind)
            {
                case ProtocolKind.Reply:
                case ProtocolKind.Error:
                    ProtocolCorrelation.Complete(corr, ProtocolEnvelope.Decode(data.ToArray()));
                    break;
                case ProtocolKind.Event:
                    runtime.ProtocolSubscriptions.Dispatch(channel, op, ProtocolEnvelope.BodyOf(data));
                    break;
            }
            return Task.CompletedTask;
        }
    }
}
