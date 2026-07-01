using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Rpc
{
    /// <summary>
    /// Server-side handler for one RPC method: receives the deserialized request and returns a response the
    /// framework serializes and sends back to the caller. Decorate the implementation with
    /// <see cref="RpcMethodAttribute"/>; it is discovered and instantiated automatically (public parameterless
    /// constructor required), like a regular SetNet message handler.
    /// </summary>
    /// <typeparam name="TRequest">The request message type (deserialized from the call).</typeparam>
    /// <typeparam name="TResponse">The response message type (serialized back to the caller).</typeparam>
    public interface IRpcHandler<in TRequest, TResponse>
    {
        /// <summary>Handles one RPC call and produces the response.</summary>
        /// <param name="peer">The peer that made the call (identify the caller / reply out-of-band if needed).</param>
        /// <param name="request">The deserialized request.</param>
        /// <returns>The response to serialize and return to the caller. Throw to send an error the caller re-throws.</returns>
        Task<TResponse> HandleAsync(BasePeer peer, TRequest request);
    }
}
