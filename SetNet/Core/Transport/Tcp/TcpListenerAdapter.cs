using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Logging;

namespace SetNet.Core.Transport.Tcp
{
    /// <summary>Server-side TCP acceptor wrapping <see cref="TcpListener"/>.</summary>
    internal sealed class TcpListenerAdapter : ITransportListener
    {
        /// <summary>The framework <see cref="TcpListener"/> that binds the local endpoint and produces accepted sockets.</summary>
        private readonly TcpListener _listener;

        /// <summary>Server configuration retained so each accepted connection inherits the configured read buffer size.</summary>
        private readonly Configuration _config;

        /// <summary>
        /// Creates a listener bound to the host/port in <paramref name="config"/>. The endpoint is bound here but
        /// not yet listening; call <see cref="Start"/> to begin accepting.
        /// </summary>
        /// <param name="config">
        /// Server settings providing the bind <see cref="Configuration.Host"/> (parsed as an IP address) and
        /// <see cref="Configuration.Port"/>, plus the <see cref="Configuration.BufferSize"/> passed to accepted connections.
        /// </param>
        /// <exception cref="FormatException">Thrown by <see cref="IPAddress.Parse"/> if <see cref="Configuration.Host"/> is not a valid IP address.</exception>
        public TcpListenerAdapter(Configuration config)
        {
            _config = config;
            _listener = new TcpListener(IPAddress.Parse(config.Host), config.Port);
        }

        /// <summary>Begins listening on the bound endpoint so that <see cref="AcceptAsync"/> can return clients.</summary>
        public void Start() => _listener.Start();

        /// <summary>
        /// Stops listening and releases the bound endpoint. Pending <see cref="AcceptAsync"/> calls observe this as an
        /// <see cref="ObjectDisposedException"/> and return <c>null</c>.
        /// </summary>
        public void Stop() => _listener.Stop();

        /// <summary>
        /// Waits for and accepts the next inbound TCP client, wrapping it in a transport connection together with its
        /// remote endpoint metadata.
        /// </summary>
        /// <param name="ct">
        /// Token signalling shutdown; when cancelled, a socket error raised by stopping the listener is treated as a
        /// clean stop rather than a fault.
        /// </param>
        /// <returns>
        /// An <see cref="AcceptedConnection"/> describing the new client, or <c>null</c> when the listener has been
        /// stopped/disposed (so the accept loop can exit cleanly).
        /// </returns>
        /// <remarks>
        /// The peer id is left as <see cref="Guid.Empty"/> here; assigning a stable identity is the caller's
        /// responsibility. Unlike UDP, TCP yields the remote endpoint directly from the accepted socket.
        /// </remarks>
        public async Task<AcceptedConnection?> AcceptAsync(CancellationToken ct = default)
        {
            // Loop internally so a single bad connection (garbage/stalled TLS handshake, a client RST mid-setup,
            // a transient accept error) is skipped rather than propagating out and killing the server's accept
            // loop. null is returned ONLY when the listener has actually stopped, which is the loop's exit signal.
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return null; }               // listener stopped
                catch (SocketException) when (ct.IsCancellationRequested) { return null; }
                catch (SocketException ex)
                {
                    // Transient accept error (e.g. ECONNABORTED after a client RST, or fd pressure under EMFILE).
                    // Do NOT tear down the accept loop — log, back off briefly, and keep accepting.
                    _config.Logger.Log($"TCP accept error (continuing): {ex.SocketErrorCode}", LogLevel.Warning);
                    try { await Task.Delay(50, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }
                    continue;
                }

                IPEndPoint? remote = null;
                try
                {
                    remote = client.Client.RemoteEndPoint as IPEndPoint;
                    var stream = await WrapServerWithTimeoutAsync(client, ct).ConfigureAwait(false);
                    var connection = new TcpConnection(client, stream, _config.BufferSize, _config.MaxMessageSize, _config.SendBatching, _config.SendBatchFlushMs, _config.SendTimeoutMs);
                    return new AcceptedConnection(connection, Guid.Empty, remote);
                }
                catch (Exception ex)
                {
                    // Per-connection setup failure (bad/garbage/stalled TLS handshake, IO error): close this
                    // client so its socket isn't leaked, count the rejection, and accept the next connection.
                    _config.Logger.Log($"Rejecting connection from {remote?.ToString() ?? "unknown"}: {ex.Message}", LogLevel.Warning);
                    _config.Metrics.IncrementConnectionsRejected();
                    try { client.Close(); } catch { /* already torn down */ }
                }
            }

            return null;
        }

        /// <summary>
        /// Performs the server-side TLS handshake with a timeout so a client that completes the TCP connect but
        /// stalls the handshake cannot occupy the accept path indefinitely (a slow-handshake DoS). Plaintext
        /// connections return immediately.
        /// </summary>
        /// <param name="client">The freshly accepted client to wrap.</param>
        /// <param name="ct">Server shutdown token, linked into the handshake timeout.</param>
        /// <returns>The negotiated stream (TLS or plaintext).</returns>
        /// <exception cref="TimeoutException">The TLS handshake did not complete within <see cref="Configuration.ConnectTimeoutMs"/>.</exception>
        private async Task<Stream> WrapServerWithTimeoutAsync(TcpClient client, CancellationToken ct)
        {
            if (!_config.UseSsl)
                return await TcpTls.WrapServerAsync(client, _config).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.ConnectTimeoutMs > 0 ? _config.ConnectTimeoutMs : 10000);

            var handshake = TcpTls.WrapServerAsync(client, _config);
            var finished = await Task.WhenAny(handshake, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
            if (finished != handshake)
                throw new TimeoutException("TLS handshake timed out."); // caller closes the client, aborting the pending handshake

            return await handshake.ConfigureAwait(false);
        }

        /// <summary>Disposes the adapter by stopping the listener and releasing its endpoint.</summary>
        public void Dispose() => Stop();
    }
}
