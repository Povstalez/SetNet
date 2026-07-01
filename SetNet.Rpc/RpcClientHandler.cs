using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Rpc
{
    /// <summary>
    /// Client-side entry point for RPC responses. Auto-discovered (a normal <c>[MessageHandler]</c> for the
    /// reserved <see cref="RpcTypes.Response"/> type), it decodes the response envelope and completes the
    /// awaiting call in <see cref="RpcRegistry"/> by correlation id. Has no connection reference and needs none —
    /// correlation ids are process-unique. The message type is <c>byte[]</c> so RPC stays serializer-agnostic.
    /// </summary>
    [MessageHandler(RpcTypes.Response)]
    public sealed class RpcClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var response = RpcFrame.Decode(data);
            RpcRegistry.Complete(response.CorrelationId, response);
            return Task.CompletedTask;
        }
    }
}
