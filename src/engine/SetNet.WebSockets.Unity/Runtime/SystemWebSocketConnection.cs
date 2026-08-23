#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport;

namespace SetNet.WebSockets.Unity
{
    /// <summary>
    /// The Standalone / Mobile / Editor path: an <see cref="ITransportConnection"/> over
    /// <see cref="ClientWebSocket"/>. Same framing as the WebGL path so a single server (SetNet.WebSockets) serves both.
    /// </summary>
    internal sealed class SystemWebSocketConnection : ITransportConnection
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly Uri _uri;
        private int _closed;

        public SystemWebSocketConnection(string url) => _uri = new Uri(url);

        public Task ConnectAsync(CancellationToken ct) => _socket.ConnectAsync(_uri, ct);

        /// <inheritdoc/>
        public bool IsConnected => _socket.State == WebSocketState.Open;

        /// <inheritdoc/>
        public TransportType Transport => TransportType.Custom;

        /// <inheritdoc/>
        public async Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            var buffer = WsFrame.Encode(type, payload);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);   // WebSocket forbids concurrent sends
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        /// <inheritdoc/>
        public async ValueTask<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var chunk = new byte[8192];
            using var assembled = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try { result = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false); }
                catch (WebSocketException) { return null; }
                catch (ObjectDisposedException) { return null; }

                if (result.MessageType == WebSocketMessageType.Close) return null;
                assembled.Write(chunk, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            var frame = assembled.ToArray();
            return WsFrame.TryDecode(frame, frame.Length, out var message) ? message : (TransportMessage?)null;
        }

        /// <inheritdoc/>
        public Task FlushAsync() => Task.CompletedTask;

        /// <inheritdoc/>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _ = _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* already dead */ }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { _socket.Dispose(); } catch { /* ignore */ }
            _sendLock.Dispose();
        }
    }
}
#endif
