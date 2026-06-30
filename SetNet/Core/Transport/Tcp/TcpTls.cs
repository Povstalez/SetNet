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
                await ssl.AuthenticateAsClientAsync(targetHost).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose(); // also closes the inner network stream (leaveInnerStreamOpen: false)
                throw;
            }
            return ssl;
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
