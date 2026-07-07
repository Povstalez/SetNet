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
            var env = ProtocolEnvelope.Decode(data);
            switch (env.Kind)
            {
                case ProtocolKind.Reply:
                case ProtocolKind.Error:
                    ProtocolCorrelation.Complete(env.Corr, env);
                    break;
                case ProtocolKind.Event:
                    runtime.ProtocolSubscriptions.Dispatch(env.Channel, env.Op, env.Body);
                    break;
            }
            return Task.CompletedTask;
        }
    }
}
