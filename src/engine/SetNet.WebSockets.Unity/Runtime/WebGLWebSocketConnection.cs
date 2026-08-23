#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport;

namespace SetNet.WebSockets.Unity
{
    /// <summary>
    /// The WebGL path: an <see cref="ITransportConnection"/> over the browser's WebSocket via the <c>SetNetWebSocket.jslib</c>
    /// bridge. WebGL is single-threaded (no <c>System.Net.WebSockets</c>), so this polls an incoming queue that the browser
    /// fills between frames. <see cref="ReceiveAsync"/> yields to the Unity player loop until a message arrives — drive your
    /// client from the main thread. Same framing as the desktop path, so one SetNet.WebSockets server serves both.
    /// </summary>
    internal sealed class WebGLWebSocketConnection : ITransportConnection
    {
        // The browser's WebSocket.readyState values.
        private const int Connecting = 0, Open = 1, Closed = 3;

        [DllImport("__Internal")] private static extern int SetNetWs_Connect(string url);
        [DllImport("__Internal")] private static extern int SetNetWs_State(int id);
        [DllImport("__Internal")] private static extern int SetNetWs_Error(int id);
        [DllImport("__Internal")] private static extern int SetNetWs_Send(int id, byte[] data, int length);
        [DllImport("__Internal")] private static extern int SetNetWs_PeekLength(int id);
        [DllImport("__Internal")] private static extern int SetNetWs_Receive(int id, byte[] buffer, int max);
        [DllImport("__Internal")] private static extern void SetNetWs_Close(int id);

        private readonly int _id;
        private int _closed;

        public WebGLWebSocketConnection(string url) => _id = SetNetWs_Connect(url);

        /// <summary>Waits for the browser to open the socket (or fail).</summary>
        public async Task ConnectAsync(CancellationToken ct)
        {
            if (_id == 0) throw new InvalidOperationException("WebGL WebSocket could not be created.");
            while (SetNetWs_State(_id) == Connecting)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();   // let the browser event loop run between frames
            }
            if (SetNetWs_State(_id) != Open)
                throw new InvalidOperationException("WebGL WebSocket failed to connect.");
        }

        /// <inheritdoc/>
        public bool IsConnected => _closed == 0 && SetNetWs_State(_id) == Open;

        /// <inheritdoc/>
        public TransportType Transport => TransportType.Custom;

        /// <inheritdoc/>
        public Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            var frame = WsFrame.Encode(type, payload);
            SetNetWs_Send(_id, frame, frame.Length);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async ValueTask<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            while (true)
            {
                var len = SetNetWs_PeekLength(_id);
                if (len < 0)
                {
                    if (SetNetWs_State(_id) == Closed) return null;   // socket closed → EOF
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();                               // no message yet → let the browser deliver one
                    continue;
                }

                var buffer = new byte[len];
                var got = SetNetWs_Receive(_id, buffer, len);
                if (got != len) { await Task.Yield(); continue; }     // rare race — retry
                return WsFrame.TryDecode(buffer, len, out var message) ? message : (TransportMessage?)null;
            }
        }

        /// <inheritdoc/>
        public Task FlushAsync() => Task.CompletedTask;

        /// <inheritdoc/>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            SetNetWs_Close(_id);
        }

        /// <inheritdoc/>
        public void Dispose() => Close();
    }
}
#endif
