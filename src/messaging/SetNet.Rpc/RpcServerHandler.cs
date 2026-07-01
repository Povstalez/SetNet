using System;
using System.Text;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Rpc
{
    /// <summary>
    /// Server-side entry point for RPC. Auto-discovered by SetNet (it's a normal <c>[MessageHandler]</c> for the
    /// reserved <see cref="RpcTypes.Request"/> type), so simply referencing this package wires RPC in — no base
    /// class, no manual registration. It decodes the request envelope, dispatches to the matching
    /// <see cref="IRpcHandler{T,T}"/>, and sends the response back over the same peer.
    /// </summary>
    /// <remarks>
    /// The message type is <c>byte[]</c>: the envelope is hand-framed, so it rides over any configured
    /// <c>ISerializer</c> (which carries a <c>byte[]</c> without needing type attributes). Only the request/response
    /// bodies inside go through the app's serializer.
    /// </remarks>
    [MessageHandler(RpcTypes.Request)]
    public sealed class RpcServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public async Task HandleAsync(BasePeer peer, byte[] data)
        {
            var request = RpcFrame.Decode(data);

            RpcFrame response;
            try
            {
                var body = await RpcMethodDispatcher.InvokeAsync(request.MethodId, peer, request.Body).ConfigureAwait(false);
                response = new RpcFrame(request.CorrelationId, request.MethodId, isError: false, body);
            }
            catch (Exception ex)
            {
                // Relay the failure to the caller as an error response rather than dropping the call.
                response = new RpcFrame(request.CorrelationId, request.MethodId, isError: true, Encoding.UTF8.GetBytes(ex.Message));
            }

            await peer.SendAsync(RpcTypes.Response, response.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
        }
    }
}
