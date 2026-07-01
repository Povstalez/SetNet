using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;

namespace SetNet.WebSockets
{
    /// <summary>
    /// The WebSocket transport, plugged into SetNet via <see cref="Configuration.CustomTransport"/>. Call
    /// <see cref="WebSocketTransportExtensions.UseWebSockets"/> on your configuration; everything above the
    /// transport (handlers, RPC, rooms, auth) then works unchanged over WebSockets.
    /// </summary>
    public sealed class WebSocketTransport : ITransportProvider
    {
        /// <inheritdoc/>
        public ITransportConnector CreateConnector(Configuration config) => new WebSocketConnector();

        /// <inheritdoc/>
        public ITransportListener CreateListener(Configuration config) => new WebSocketListener(config);
    }

    /// <summary>Fluent helper for enabling the WebSocket transport.</summary>
    public static class WebSocketTransportExtensions
    {
        /// <summary>Switches the configuration to the WebSocket transport (sets <see cref="TransportType.Custom"/> + a <see cref="WebSocketTransport"/>).</summary>
        /// <param name="config">The configuration to modify.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static Configuration UseWebSockets(this Configuration config)
        {
            config.TransportType = TransportType.Custom;
            config.CustomTransport = new WebSocketTransport();
            return config;
        }
    }

    /// <summary>Client-side WebSocket dialer.</summary>
    internal sealed class WebSocketConnector : ITransportConnector
    {
        /// <inheritdoc/>
        public async Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
        {
            var socket = new ClientWebSocket();
            var uri = new Uri($"ws://{config.Host}:{config.Port}/");
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new WebSocketConnection(socket);
        }
    }

    /// <summary>Server-side WebSocket acceptor over <see cref="HttpListener"/> (HTTP upgrade to WebSocket).</summary>
    internal sealed class WebSocketListener : ITransportListener
    {
        private readonly HttpListener _http = new HttpListener();

        public WebSocketListener(Configuration config)
        {
            // "0.0.0.0"/empty binds all interfaces via "+", which needs a urlacl/admin on Windows; a specific
            // host (e.g. 127.0.0.1) binds without elevation.
            var host = string.IsNullOrEmpty(config.Host) || config.Host == "0.0.0.0" ? "+" : config.Host;
            _http.Prefixes.Add($"http://{host}:{config.Port}/");
        }

        /// <inheritdoc/>
        public void Start() => _http.Start();

        /// <inheritdoc/>
        public void Stop() { try { _http.Stop(); } catch { /* ignore */ } }

        /// <inheritdoc/>
        public async Task<AcceptedConnection?> AcceptAsync(CancellationToken ct = default)
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = await _http.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return null; }   // listener stopped/disposed → no more connections

                if (!context.Request.IsWebSocketRequest)
                {
                    // Not a WebSocket upgrade — reject and keep listening.
                    try { context.Response.StatusCode = 400; context.Response.Close(); } catch { /* ignore */ }
                    continue;
                }

                HttpListenerWebSocketContext wsContext;
                try { wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false); }
                catch { continue; }   // bad handshake — skip and keep listening (resilient accept)

                return new AcceptedConnection(new WebSocketConnection(wsContext.WebSocket), Guid.Empty, context.Request.RemoteEndPoint);
            }
        }

        /// <inheritdoc/>
        public void Dispose() { try { _http.Close(); } catch { /* ignore */ } }
    }
}
