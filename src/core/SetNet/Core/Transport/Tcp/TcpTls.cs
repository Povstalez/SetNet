using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Tcp
{
    /// <summary>
    /// Optional TLS layering for the TCP transport. When <see cref="Configuration.UseSsl"/> is enabled the raw
    /// <see cref="NetworkStream"/> is wrapped in an <see cref="SslStream"/> and the handshake is performed;
    /// otherwise the plaintext network stream is returned unchanged. UDP traffic is never encrypted here.
    /// </summary>
    internal static class TcpTls
    {
        /// <summary>
        /// Client side: returns the plaintext stream, or — when TLS is enabled — an authenticated
        /// <see cref="SslStream"/> validating the server's certificate for the configured target host.
        /// </summary>
        /// <param name="client">The connected TCP client whose stream is wrapped.</param>
        /// <param name="config">Connection settings (TLS toggle, target host, optional validation callback).</param>
        /// <param name="ct">Cancellation token for the surrounding connect operation.</param>
        /// <returns>The stream to use for I/O: the raw network stream or a TLS-wrapped stream.</returns>
        /// <exception cref="System.Security.Authentication.AuthenticationException">The TLS handshake or certificate validation failed.</exception>
        /// <exception cref="TimeoutException">The handshake did not complete within <see cref="Configuration.ConnectTimeoutMs"/>.</exception>
        /// <remarks>
        /// The handshake is bounded by <see cref="Configuration.ConnectTimeoutMs"/> (and by <paramref name="ct"/>).
        /// <c>AuthenticateAsClientAsync</c> has no deadline of its own, so a server that accepts the TCP connection
        /// and then goes silent mid-handshake would otherwise hang connect — and every auto-reconnect attempt —
        /// forever, with no callback ever firing.
        /// </remarks>
        public static async Task<Stream> WrapClientAsync(TcpClient client, Configuration config, CancellationToken ct = default)
        {
            Stream network = client.GetStream();
            if (!config.UseSsl) return network;

            var ssl = config.ServerCertificateValidationCallback != null
                ? new SslStream(network, leaveInnerStreamOpen: false, config.ServerCertificateValidationCallback)
                : new SslStream(network, leaveInnerStreamOpen: false);

            var targetHost = string.IsNullOrEmpty(config.SslTargetHost) ? config.Host : config.SslTargetHost;
            try
            {
                await WithTimeoutAsync(
                    ssl.AuthenticateAsClientAsync(targetHost), ssl, config.ConnectTimeoutMs, ct).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose(); // also closes the inner network stream (leaveInnerStreamOpen: false)
                throw;
            }
            return ssl;
        }

        /// <summary>
        /// Bounds a TLS handshake that offers no deadline of its own. Disposing the <see cref="SslStream"/> is what
        /// actually aborts a handshake parked on a socket read — cancellation tokens do not reliably interrupt one —
        /// so on timeout the stream is disposed and the abandoned handshake is awaited to completion before throwing.
        /// </summary>
        /// <param name="handshake">The in-flight handshake task.</param>
        /// <param name="ssl">The stream to dispose in order to abort the handshake.</param>
        /// <param name="timeoutMs">Deadline in milliseconds; 0 or less waits indefinitely.</param>
        /// <param name="ct">Caller cancellation, treated the same as a timeout.</param>
        private static async Task WithTimeoutAsync(Task handshake, SslStream ssl, int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0)
            {
                await handshake.ConfigureAwait(false);
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeoutMs, timeoutCts.Token);
            if (await Task.WhenAny(handshake, delay).ConfigureAwait(false) == handshake)
            {
                timeoutCts.Cancel(); // release the pending delay
                await handshake.ConfigureAwait(false);
                return;
            }

            ssl.Dispose(); // faults the parked handshake
            try { await handshake.ConfigureAwait(false); } catch { /* expected: aborted by the dispose */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"TLS handshake timed out after {timeoutMs}ms.");
        }

        /// <summary>
        /// Server side: returns the plaintext stream, or — when TLS is enabled — an authenticated
        /// <see cref="SslStream"/> presenting the configured server certificate.
        /// </summary>
        /// <param name="client">The accepted TCP client whose stream is wrapped.</param>
        /// <param name="config">Listener settings (TLS toggle and server certificate).</param>
        /// <returns>The stream to use for I/O: the raw network stream or a TLS-wrapped stream.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Configuration.UseSsl"/> is enabled but no <see cref="Configuration.ServerCertificate"/> was provided.</exception>
        public static async Task<Stream> WrapServerAsync(TcpClient client, Configuration config)
        {
            Stream network = client.GetStream();
            if (!config.UseSsl) return network;

            if (config.ServerCertificate == null)
                throw new InvalidOperationException(
                    "Configuration.UseSsl is enabled but Configuration.ServerCertificate is not set.");

            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(config.ServerCertificate).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose(); // also closes the inner network stream (leaveInnerStreamOpen: false)
                throw;
            }
            return ssl;
        }
    }
}
