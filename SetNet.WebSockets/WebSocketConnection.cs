using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport;

namespace SetNet.WebSockets
{
    /// <summary>
    /// An <see cref="ITransportConnection"/> over a single <see cref="WebSocket"/>. Each application message is one
    /// binary WebSocket message framed as <c>[2-byte type][payload]</c> — WebSocket message boundaries replace the
    /// length prefix TCP needs. WebSockets are reliable and ordered, so <see cref="DeliveryMethod"/>/channel are ignored.
    /// </summary>
    internal sealed class WebSocketConnection : ITransportConnection
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private int _closed;

        public WebSocketConnection(WebSocket socket) => _socket = socket;

        /// <inheritdoc/>
        public bool IsConnected => _socket.State == WebSocketState.Open;

        /// <inheritdoc/>
        public TransportType Transport => TransportType.Custom;

        /// <inheritdoc/>
        public async Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            payload = payload ?? Array.Empty<byte>();
            var buffer = new byte[2 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), type);
            Buffer.BlockCopy(payload, 0, buffer, 2, payload.Length);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);   // WebSocket forbids concurrent sends
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        /// <inheritdoc/>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var chunk = new byte[8192];
            using var assembled = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try { result = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false); }
                catch (WebSocketException) { return null; }        // broken socket → EOF
                catch (ObjectDisposedException) { return null; }

                if (result.MessageType == WebSocketMessageType.Close) return null;   // graceful close → EOF
                assembled.Write(chunk, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            var frame = assembled.ToArray();
            if (frame.Length < 2) return null;                     // malformed; treat as close
            var type = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
            var payload = new byte[frame.Length - 2];
            Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
            return new TransportMessage(type, payload);
        }

        /// <inheritdoc/>
        public Task FlushAsync() => Task.CompletedTask;

        /// <inheritdoc/>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            // Best-effort graceful close; ignore failures (already-dead socket).
            try { _ = _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* ignore */ }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { _socket.Dispose(); } catch { /* ignore */ }
            _sendLock.Dispose();
        }
    }
}
