using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

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
            try
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                var stream = await TcpTls.WrapServerAsync(client, _config).ConfigureAwait(false);
                var connection = new TcpConnection(client, stream, _config.BufferSize, _config.MaxMessageSize, _config.SendBatching, _config.SendBatchFlushMs);
                return new AcceptedConnection(connection, Guid.Empty, remote);
            }
            catch (ObjectDisposedException) { return null; }
            catch (SocketException) when (ct.IsCancellationRequested) { return null; }
        }

        /// <summary>Disposes the adapter by stopping the listener and releasing its endpoint.</summary>
        public void Dispose() => Stop();
    }
}
