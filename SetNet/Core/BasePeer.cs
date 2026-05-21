using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Messaging;

namespace SetNet.Core
{

    public abstract class BasePeer : BaseSocket
    {
        protected readonly PeerInfo CurrentPeerInfo;
        private volatile bool _isIntentionalClose;

        protected BasePeer(PeerInfo currentPeerInfo) : base()
        {
            CurrentPeerInfo = currentPeerInfo;
            Stream = currentPeerInfo.Client.GetStream();
        }

        public void StartReceive()
        {
            RegisterDataHandlers();
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
                if (!_isIntentionalClose)
                    OnError($"Client {CurrentPeerInfo.Id} disconnected due to IO error.");
            }
            catch (SocketException)
            {
                hadError = true;
                if (!_isIntentionalClose)
                    OnError($"Client {CurrentPeerInfo.Id} disconnected due to socket error.");
            }
            catch (ObjectDisposedException)
            {
                hadError = true;
                if (!_isIntentionalClose)
                    OnError($"Client {CurrentPeerInfo.Id} connection was closed.");
            }
            catch (Exception ex)
            {
                hadError = true;
                if (!_isIntentionalClose)
                    OnError($"Client {CurrentPeerInfo.Id} error: {ex.Message}");
            }
            finally
            {
                if (_isIntentionalClose)
                {
                    _isIntentionalClose = false;
                }
                else if (hadError)
                {
                    OnUnexpectedDisconnect();
                    Close();
                }
                else
                {
                    Close();
                }
            }
        }

        protected async Task SendAsync<T>(ushort type, T message)
        {
            var data = MessagePackSerializer.Serialize(message);
            var packet = PacketBuilder.BuildPacket(type, data);

            await SendAsync(packet);
        }

        protected async Task SendAsync(byte[] data)
        {
            await Stream.WriteAsync(data, 0, data.Length);
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
            {
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
            }
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