using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Rpc
{
    /// <summary>
    /// RPC calling surface, added to <see cref="BaseClient"/> by extension (no <c>RpcClient</c> base class) so it
    /// sits alongside your regular <c>SendAsync</c> and message handlers. Reference the package and call
    /// <see cref="CallAsync{TRequest,TResponse}"/>.
    /// </summary>
    public static class RpcClientExtensions
    {
        /// <summary>
        /// Invokes an RPC method on the server and awaits its typed response. The request is serialized with the
        /// app's configured serializer, sent reliably, and matched to its response by a correlation id.
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

            var correlationId = RpcRegistry.NextId();
            var tcs = new TaskCompletionSource<RpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            RpcRegistry.Register(correlationId, tcs);
            try
            {
                var envelope = new RpcEnvelope
                {
                    CorrelationId = correlationId,
                    MethodId = methodId,
                    IsError = false,
                    Body = SetNetSerializer.Serialize(request)
                };
                await client.SendAsync(RpcTypes.Request, envelope, DeliveryMethod.Reliable).ConfigureAwait(false);

                RpcEnvelope response;
                if (timeoutMs <= 0 && !cancellationToken.CanBeCanceled)
                {
                    response = await tcs.Task.ConfigureAwait(false);
                }
                else
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (timeoutMs > 0) linked.CancelAfter(timeoutMs);
                    using (linked.Token.Register(() => tcs.TrySetCanceled()))
                    {
                        try
                        {
                            response = await tcs.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException($"RPC method {methodId} timed out after {timeoutMs} ms.");
                        }
                    }
                }

                if (response.IsError)
                    throw new RpcException(Encoding.UTF8.GetString(response.Body ?? Array.Empty<byte>()));

                return SetNetSerializer.Deserialize<TResponse>(response.Body);
            }
            finally
            {
                RpcRegistry.Remove(correlationId);
            }
        }
    }
}
