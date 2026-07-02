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
                    await ctx.ReplyErrorAsync($"No protocol channel {env.Channel} is configured on this server.").ConfigureAwait(false);
                return;
            }

            try
            {
                await service.HandleAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Relay the failure to a waiting caller instead of dropping the request.
                if (ctx.ExpectsReply)
                    await ctx.ReplyErrorAsync(ex.Message).ConfigureAwait(false);
            }
        }

        /// <summary>Handles an inbound envelope on the client: complete an awaiting request, or dispatch a push event.</summary>
        public static Task DispatchClientAsync(byte[] data)
        {
            var env = ProtocolEnvelope.Decode(data);
            switch (env.Kind)
            {
                case ProtocolKind.Reply:
                case ProtocolKind.Error:
                    ProtocolCorrelation.Complete(env.Corr, env);
                    break;
                case ProtocolKind.Event:
                    ProtocolSubscriptions.Dispatch(env.Channel, env.Op, env.Body);
                    break;
            }
            return Task.CompletedTask;
        }
    }
}
