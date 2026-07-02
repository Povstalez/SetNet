using System.Threading.Tasks;
using SetNet.Protocol;

namespace SetNet.Rpc
{
    /// <summary>
    /// Server-side entry point for RPC under the unified protocol: an auto-discovered channel service on
    /// <see cref="Channels.Rpc"/> where the op is the RPC method id. It hands the request body to the discovered
    /// <see cref="IRpcHandler{TRequest,TResponse}"/> (via <see cref="RpcMethodDispatcher"/>) and replies with the
    /// serialized response. A throwing handler — or an unknown method id — propagates out and is relayed to the
    /// caller as an error reply (surfacing as an <see cref="RpcException"/>). RPC is therefore the same request/reply
    /// mechanism as <c>client.RequestAsync</c>, just with a typed method-id front end.
    /// </summary>
    [ProtocolChannel(Channels.Rpc)]
    public sealed class RpcChannelService : IChannelService
    {
        /// <inheritdoc/>
        public async Task HandleAsync(ChannelRequest request)
        {
            var responseBody = await RpcMethodDispatcher.InvokeAsync(request.Op, request.Peer, request.RawBody).ConfigureAwait(false);
            await request.ReplyRawAsync(responseBody).ConfigureAwait(false);
        }
    }
}
