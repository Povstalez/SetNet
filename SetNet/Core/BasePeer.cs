using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Messaging;

namespace SetNet.Core
{
    public abstract class BasePeer : BaseSocket
    {
        protected readonly PeerInfo CurrentPeerInfo;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private volatile bool _isIntentionalClose;
        private volatile bool _isHeartbeatTimeoutClose;
        private long _lastPingReceivedTicks;

        protected BasePeer(PeerInfo currentPeerInfo) : base()
        {
            CurrentPeerInfo = currentPeerInfo;
            Stream = currentPeerInfo.Client.GetStream();
        }

        public void StartReceive()
        {
            RegisterDataHandlers();

            if (CurrentPeerInfo.Config.HeartbeatEnabled)
            {
                Interlocked.Exchange(ref _lastPingReceivedTicks, DateTime.UtcNow.Ticks);
                RegisterDataHandler(SystemMessageTypes.Ping, OnPingReceived);
                _ = HeartbeatTimeoutCheckAsync();
            }

            _ = HandlePeerAsync();
        }

        private async Task HandlePeerAsync()
        {
            var buffer = new byte[CurrentPeerInfo.Config.BufferSize];
            var hadError = false;

            try
            {
                while (CurrentPeerInfo.Client.Connected)
                {
                    var bytesRead = await Stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    PacketBuilder.AppendData(buffer[..bytesRead]);

                    while (PacketBuilder.TryGetCompletePacket(out var packet))
                    {
                        var (type, data) = PacketBuilder.ParsePacket(packet);
                        HandleMessage(type, data);
                    }
                }
            }
            catch (IOException)
            {
                hadError = true;
                if (!_isIntentionalClose && !_isHeartbeatTimeoutClose)
                    OnError($"Client {CurrentPeerInfo.Id} disconnected due to IO error.");
            }
            catch (SocketException)
            {
                hadError = true;
                if (!_isIntentionalClose && !_isHeartbeatTimeoutClose)
                    OnError($"Client {CurrentPeerInfo.Id} disconnected due to socket error.");
            }
            catch (ObjectDisposedException)
            {
                hadError = true;
                if (!_isIntentionalClose && !_isHeartbeatTimeoutClose)
                    OnError($"Client {CurrentPeerInfo.Id} connection was closed.");
            }
            catch (Exception ex)
            {
                hadError = true;
                if (!_isIntentionalClose && !_isHeartbeatTimeoutClose)
                    OnError($"Client {CurrentPeerInfo.Id} error: {ex.Message}");
            }
            finally
            {
                var wasHeartbeat = _isHeartbeatTimeoutClose;
                _isHeartbeatTimeoutClose = false;

                if (_isIntentionalClose)
                    _isIntentionalClose = false;
                else if (hadError || wasHeartbeat)
                {
                    OnUnexpectedDisconnect();
                    Close();
                }
                else
                    Close();
            }
        }

        private void OnPingReceived(byte[] data)
        {
            Interlocked.Exchange(ref _lastPingReceivedTicks, DateTime.UtcNow.Ticks);
            var packet = PacketBuilder.BuildPacket(SystemMessageTypes.Pong, Array.Empty<byte>());
            _ = SendAsync(packet);
        }

        private async Task HeartbeatTimeoutCheckAsync()
        {
            try
            {
                await Task.Delay(CurrentPeerInfo.Config.HeartbeatTimeoutMs);

                while (CurrentPeerInfo.Client.Connected)
                {
                    var elapsed = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPingReceivedTicks))
                                  / TimeSpan.TicksPerMillisecond;

                    if (elapsed > CurrentPeerInfo.Config.HeartbeatTimeoutMs)
                    {
                        _isHeartbeatTimeoutClose = true;
                        OnError($"Client {CurrentPeerInfo.Id} heartbeat timeout.");
                        CurrentPeerInfo.Client.Close();
                        return;
                    }

                    await Task.Delay(CurrentPeerInfo.Config.HeartbeatIntervalMs);
                }
            }
            catch { }
        }

        protected async Task SendAsync<T>(ushort type, T message)
        {
            var data = MessagePackSerializer.Serialize(message);
            var packet = PacketBuilder.BuildPacket(type, data);
            await SendAsync(packet);
        }

        protected async Task SendAsync(byte[] data)
        {
            await _writeLock.WaitAsync();
            try
            {
                await Stream.WriteAsync(data, 0, data.Length);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public virtual void Close()
        {
            _isIntentionalClose = true;
            CurrentPeerInfo.Disconnect();
            OnDisconnected();
        }

        protected virtual void RegisterDataHandlers()
        {
            foreach (var messageType in CurrentPeerInfo.CommandExecutor.Keys)
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
        }

        protected abstract void OnDisconnected();
        protected virtual void OnError(string error) { }
        protected virtual void OnUnexpectedDisconnect() { }

        private Func<byte[], Task> CreateHandlerDelegate(ushort messageType)
        {
            return async data => await CurrentPeerInfo.CommandExecutor.Handlers[messageType].HandleAsync(this, data);
        }
    }
}