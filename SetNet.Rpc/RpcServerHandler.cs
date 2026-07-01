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
    /// class, no manual registration. It dispatches the request to the matching <see cref="IRpcHandler{T,T}"/>
    /// and sends the response back over the same peer.
    /// </summary>
    [MessageHandler(RpcTypes.Request)]
    public sealed class RpcServerHandler : IServerMessageHandler<RpcEnvelope>
    {
        /// <inheritdoc/>
        public async Task HandleAsync(BasePeer peer, RpcEnvelope request)
        {
            RpcEnvelope response;
            try
            {
                var body = await RpcMethodDispatcher.InvokeAsync(request.MethodId, peer, request.Body).ConfigureAwait(false);
                response = new RpcEnvelope
                {
                    CorrelationId = request.CorrelationId,
                    MethodId = request.MethodId,
                    IsError = false,
                    Body = body
                };
            }
            catch (Exception ex)
            {
                // Relay the failure to the caller as an error response rather than dropping the call.
                response = new RpcEnvelope
                {
                    CorrelationId = request.CorrelationId,
                    MethodId = request.MethodId,
                    IsError = true,
                    Body = Encoding.UTF8.GetBytes(ex.Message)
                };
            }

            await peer.SendAsync(RpcTypes.Response, response, DeliveryMethod.Reliable).ConfigureAwait(false);
        }
    }
}
