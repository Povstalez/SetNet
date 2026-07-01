using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Rpc
{
    /// <summary>
    /// Client-side entry point for RPC responses. Auto-discovered (a normal <c>[MessageHandler]</c> for the
    /// reserved <see cref="RpcTypes.Response"/> type), it completes the awaiting call in
    /// <see cref="RpcRegistry"/> by correlation id. Has no connection reference and needs none — correlation ids
    /// are process-unique.
    /// </summary>
    [MessageHandler(RpcTypes.Response)]
    public sealed class RpcClientHandler : IClientMessageHandler<RpcEnvelope>
    {
        /// <inheritdoc/>
        public Task HandleAsync(RpcEnvelope response)
        {
            RpcRegistry.Complete(response.CorrelationId, response);
            return Task.CompletedTask;
        }
    }
}
