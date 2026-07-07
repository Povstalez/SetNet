using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;

namespace SetNet.WebSockets.Unity
{
    /// <summary>Enables the Unity WebSocket transport on a <see cref="Configuration"/>.</summary>
    public static class UnityWebSocketExtensions
    {
        /// <summary>
        /// Switches the configuration to a WebSocket transport that works on <b>every Unity platform, including WebGL</b>
        /// (browser). On WebGL it uses a <c>.jslib</c> bridge to the browser's WebSocket; on Standalone/Mobile/Editor it
        /// uses <see cref="System.Net.WebSockets.ClientWebSocket"/>. Client-only — host the server with the .NET
        /// <c>SetNet.WebSockets</c> package.
        /// </summary>
        /// <param name="config">The configuration to modify.</param>
        /// <param name="secure">Use <c>wss://</c> (TLS). Required for WebGL served over https (e.g. Telegram Mini Apps).</param>
        public static Configuration UseUnityWebSockets(this Configuration config, bool secure = false)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.TransportType = TransportType.Custom;
            config.CustomTransport = new UnityWebSocketTransport(secure || config.UseSsl);
            return config;
        }
    }

    /// <summary>The Unity WebSocket <see cref="ITransportProvider"/> (client-only).</summary>
    public sealed class UnityWebSocketTransport : ITransportProvider
    {
        private readonly bool _secure;

        /// <summary>Creates the provider.</summary>
        public UnityWebSocketTransport(bool secure = false) => _secure = secure;

        /// <inheritdoc/>
        public ITransportConnector CreateConnector(Configuration config) => new UnityWebSocketConnector(_secure);

        /// <inheritdoc/>
        public ITransportListener CreateListener(Configuration config)
            => throw new NotSupportedException(
                "The Unity WebSocket transport is client-only. Run the server with the .NET SetNet.WebSockets package.");
    }

    /// <summary>Client-side dialer that picks the WebGL or the System.Net implementation at compile time.</summary>
    internal sealed class UnityWebSocketConnector : ITransportConnector
    {
        private readonly bool _secure;

        public UnityWebSocketConnector(bool secure) => _secure = secure;

        /// <inheritdoc/>
        public async Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
        {
            var scheme = _secure ? "wss" : "ws";
            var url = $"{scheme}://{config.Host}:{config.Port}/";
#if UNITY_WEBGL && !UNITY_EDITOR
            var connection = new WebGLWebSocketConnection(url);
#else
            var connection = new SystemWebSocketConnection(url);
#endif
            await connection.ConnectAsync(ct).ConfigureAwait(false);
            return connection;
        }
    }

    /// <summary>Shared framing helpers: one binary WS message == <c>[2-byte type LE][payload]</c>.</summary>
    internal static class WsFrame
    {
        public static byte[] Encode(ushort type, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var buffer = new byte[2 + payload.Length];
            buffer[0] = (byte)(type & 0xFF);
            buffer[1] = (byte)(type >> 8);
            Buffer.BlockCopy(payload, 0, buffer, 2, payload.Length);
            return buffer;
        }

        public static bool TryDecode(byte[] frame, int length, out TransportMessage message)
        {
            if (frame == null || length < 2) { message = default; return false; }
            var type = (ushort)(frame[0] | (frame[1] << 8));
            var payload = new byte[length - 2];
            Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
            message = new TransportMessage(type, payload);
            return true;
        }
    }
}
