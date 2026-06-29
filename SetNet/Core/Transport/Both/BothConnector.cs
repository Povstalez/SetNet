using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport.Tcp;
using SetNet.Core.Transport.Udp;
using SetNet.Logging;

namespace SetNet.Core.Transport.Both
{
    /// <summary>
    /// Client-side connector for Both mode: connect TCP, receive the server-issued UDP bind token
    /// over TCP, then run the UDP handshake with that token. If UDP is unavailable the connection
    /// degrades gracefully to TCP-only.
    /// </summary>
    internal sealed class BothConnector : ITransportConnector
    {
        /// <summary>
        /// Establishes a Both-mode connection: opens TCP first, reads the server-issued UDP bind token over that
        /// reliable channel, then attempts the token-authenticated UDP handshake, gracefully degrading to TCP-only
        /// if UDP cannot be established.
        /// </summary>
        /// <param name="config">Connection settings including host, ports, and timeouts; also supplies the <see cref="Configuration.Logger"/> used for the fallback warning.</param>
        /// <param name="ct">Token used to cancel the connect sequence.</param>
        /// <returns>A composite <see cref="BothConnection"/> wrapping the TCP channel and, when available, the UDP channel.</returns>
        /// <remarks>
        /// On any failure after TCP is open (e.g. the bind token never arrives), the TCP channel is closed before
        /// the exception propagates, so no half-open socket is leaked. A UDP handshake failure is non-fatal and is
        /// logged at <see cref="LogLevel.Warning"/> rather than thrown.
        /// </remarks>
        public async Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
        {
            var tcp = await new TcpConnector().ConnectAsync(config, ct).ConfigureAwait(false);
            try
            {
                var token = await WaitForBindTokenAsync(tcp, config, ct).ConfigureAwait(false);

                ITransportConnection? udp = null;
                try
                {
                    udp = await UdpClientConnector.ConnectWithTokenAsync(config, token, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    config.Logger.Log(
                        $"Both mode: UDP unavailable, falling back to TCP for all traffic. ({ex.Message})",
                        LogLevel.Warning);
                }

                return new BothConnection(tcp, udp);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        /// <summary>
        /// Reads frames off the freshly opened TCP channel until the server's UDP bind token arrives, enforcing a
        /// timeout so a silent server cannot stall the connect indefinitely.
        /// </summary>
        /// <param name="tcp">The connected TCP channel to read the bind token from.</param>
        /// <param name="config">Configuration supplying <see cref="Configuration.ConnectTimeoutMs"/> (defaulting to 10 seconds when not positive).</param>
        /// <param name="ct">Caller's cancellation token, linked with the internal timeout.</param>
        /// <returns>The <see cref="Guid"/> bind token the client must echo during the UDP handshake.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the TCP connection closes before the bind token is received.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the caller cancels or the connect timeout elapses.</exception>
        /// <remarks>
        /// Wire protocol: the server sends the <see cref="SystemMessageTypes.UdpBindToken"/> frame (a 16-byte GUID
        /// payload) first; any other early frame is ignored so the handshake is robust to reordering or stray traffic.
        /// </remarks>
        private static async Task<Guid> WaitForBindTokenAsync(ITransportConnection tcp, Configuration config, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(config.ConnectTimeoutMs > 0 ? config.ConnectTimeoutMs : 10000);

            while (true)
            {
                var msg = await tcp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (msg == null)
                    throw new InvalidOperationException("TCP connection closed before the UDP bind token was received.");

                var m = msg.Value;
                if (m.Type == SystemMessageTypes.UdpBindToken && m.Payload.Length >= 16)
                    return new Guid(m.Payload);
                // Ignore any other early frame (server sends the bind token first).
            }
        }
    }
}
