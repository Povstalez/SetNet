using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Tcp
{
    /// <summary>Client-side TCP dialer with connect timeout (preserves original behaviour).</summary>
    internal sealed class TcpConnector : ITransportConnector
    {
        /// <summary>
        /// Opens a TCP connection to the configured host and port, enforcing an optional connect timeout, and
        /// wraps the resulting socket in a <see cref="TcpConnection"/> ready for sending and receiving.
        /// </summary>
        /// <param name="config">
        /// Connection settings supplying the target <see cref="Configuration.Host"/>/<see cref="Configuration.Port"/>,
        /// the <see cref="Configuration.ConnectTimeoutMs"/> deadline, and the read <see cref="Configuration.BufferSize"/>.
        /// </param>
        /// <param name="ct">Token to cancel the timeout delay (and thus abandon a slow connect attempt).</param>
        /// <returns>An established <see cref="ITransportConnection"/> backed by the freshly connected socket.</returns>
        /// <exception cref="TimeoutException">
        /// Thrown when <see cref="Configuration.ConnectTimeoutMs"/> is positive and the connect does not complete in time.
        /// </exception>
        /// <remarks>
        /// The timeout is implemented by racing the connect task against a delay; on timeout the half-open socket
        /// is closed before throwing. Awaiting the connect task afterwards re-throws any genuine connect failure
        /// (e.g. connection refused) rather than masking it.
        /// </remarks>
        public async Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
        {
            var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(config.Host, config.Port);

                if (config.ConnectTimeoutMs > 0)
                {
                    if (await Task.WhenAny(connectTask, Task.Delay(config.ConnectTimeoutMs, ct)).ConfigureAwait(false) != connectTask)
                        throw new TimeoutException($"Connection timed out after {config.ConnectTimeoutMs}ms");
                }

                await connectTask.ConfigureAwait(false); // surface connect errors

                var stream = await TcpTls.WrapClientAsync(client, config, ct).ConfigureAwait(false);
                return new TcpConnection(client, stream, config.BufferSize, config.MaxMessageSize, config.SendBatching, config.SendBatchFlushMs, config.SendTimeoutMs);
            }
            catch
            {
                // Connect/timeout/TLS-handshake failure: close the socket so a failed (or retried) connect never
                // leaks a file descriptor. Ownership only transfers to TcpConnection on the success path above.
                try { client.Close(); } catch { /* already torn down */ }
                throw;
            }
        }
    }
}
