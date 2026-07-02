using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Rpc
{
    /// <summary>
    /// RPC calling surface, added to <see cref="BaseClient"/> by extension (no <c>RpcClient</c> base class) so it
    /// sits alongside your regular messages. <c>CallAsync</c> is a thin, typed method-id front end over the unified
    /// protocol's <c>client.RequestAsync</c> — the exact same request/reply mechanism (one envelope, one correlation
    /// registry), carried on the reserved <see cref="Channels.Rpc"/> channel with the RPC method id as the op.
    /// </summary>
    public static class RpcClientExtensions
    {
        /// <summary>
        /// Invokes an RPC method on the server and awaits its typed response. The request is serialized with the
        /// app's configured serializer, sent reliably on the <see cref="Channels.Rpc"/> channel (op = method id),
        /// and matched to its response by the shared protocol correlation.
        /// </summary>
        /// <typeparam name="TRequest">The request type.</typeparam>
        /// <typeparam name="TResponse">The expected response type.</typeparam>
        /// <param name="client">The connected client to call over.</param>
        /// <param name="methodId">The RPC method id (matches a server-side <see cref="RpcMethodAttribute"/>).</param>
        /// <param name="request">The request payload.</param>
        /// <param name="timeoutMs">Per-call timeout in milliseconds; 0 or less waits indefinitely. Default 5000.</param>
        /// <param name="cancellationToken">Cancels the wait for a response.</param>
        /// <returns>The deserialized response.</returns>
        /// <exception cref="RpcException">The server-side handler threw, or no handler is registered for the method id.</exception>
        /// <exception cref="TimeoutException">No response arrived within <paramref name="timeoutMs"/>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
        public static async Task<TResponse> CallAsync<TRequest, TResponse>(
            this BaseClient client,
            ushort methodId,
            TRequest request,
            int timeoutMs = 5000,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            try
            {
                return await client.RequestAsync<TRequest, TResponse>(Channels.Rpc, methodId, request, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                // The unified protocol reports a failed request as ProtocolException; RPC's public contract is RpcException.
                throw new RpcException(ex.Message);
            }
        }
    }
}
