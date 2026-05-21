using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Commands;
using SetNet.Data;
using SetNet.Messaging;

namespace SetNet.Core
{

    public abstract class BaseClient : BaseSocket
    {
        private readonly Configuration _config;
        private TcpClient _client;
        private CommandExecutor<IClientMessageHandler> _commandExecutor;

        private CancellationTokenSource _cancellationTokenSource;
        private volatile bool _isIntentionalDisconnect;

        protected BaseClient(Configuration config) : base()
        {
            _config = config;

            _commandExecutor = new CommandExecutor<IClientMessageHandler>();
        }

        public async Task ConnectAsync()
        {
            _isIntentionalDisconnect = false;
            RegisterDataHandlers();

            _cancellationTokenSource = new CancellationTokenSource();

            _client = new TcpClient();
            await _client.ConnectAsync(_config.Host, _config.Port);
            Stream = _client.GetStream();

            _ = ReceiveAsync(_client);
            OnConnected();
        }

        public void Disconnect()
        {
            if(_cancellationTokenSource.Token.IsCancellationRequested)
                return;

            _isIntentionalDisconnect = true;
            _cancellationTokenSource.Cancel();
            _client?.Close();
            OnDisconnected();
        }

        private async Task ReceiveAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[_config.BufferSize];
            var packetBuilder = new PacketBuilder();

            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    packetBuilder.AppendData(buffer[..bytesRead]);

                    while (packetBuilder.TryGetCompletePacket(out var packet))
                    {
                        var (type, data) = PacketBuilder.ParsePacket(packet);
                        LogNewMessage(type);
                        HandleMessage(type, data);
                    }
                }
            }
            catch (IOException)
            {
                if (!_isIntentionalDisconnect)
                    OnError("Connection lost due to IO error.");
            }
            catch (SocketException)
            {
                if (!_isIntentionalDisconnect)
                    OnError("Connection lost due to socket error.");
            }
            catch (Exception ex)
            {
                if (!_isIntentionalDisconnect)
                    OnError($"Error: {ex.Message}");
            }
            finally
            {
                if (_isIntentionalDisconnect)
                {
                    _isIntentionalDisconnect = false;
                }
                else
                {
                    _client?.Close();
                    OnUnexpectedDisconnect();

                    if (_config.AutoReconnect)
                        _ = ReconnectAsync();
                    else
                        OnDisconnected();
                }
            }
        }

        protected async Task SendAsync<T>(ushort type, T message)
        {
            var data = MessagePackSerializer.Serialize(message);
            var packet = PacketBuilder.BuildPacket(type, data);

            await SendAsync(packet);
        }

        private async Task SendAsync(byte[] data)
        {
            await Stream.WriteAsync(data, 0, data.Length);
        }

        private async Task ReconnectAsync()
        {
            for (int attempt = 1; attempt <= _config.MaxReconnectAttempts; attempt++)
            {
                OnReconnecting(attempt, _config.MaxReconnectAttempts);
                await Task.Delay(_config.ReconnectDelayMs);

                try
                {
                    _isIntentionalDisconnect = false;
                    _cancellationTokenSource = new CancellationTokenSource();
                    _client = new TcpClient();
                    await _client.ConnectAsync(_config.Host, _config.Port);
                    Stream = _client.GetStream();

                    _ = ReceiveAsync(_client);
                    OnReconnected();
                    return;
                }
                catch { }
            }

            OnReconnectFailed();
            OnDisconnected();
        }

        protected virtual void RegisterDataHandlers()
        {
            foreach (var messageType in _commandExecutor.Keys)
            {
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
            }
        }
        
        private Func<byte[], Task> CreateHandlerDelegate(ushort messageType)
        {
            return async data => await _commandExecutor.Handlers[messageType].HandleAsync(data);
        }

        protected abstract void OnConnected();
        protected abstract void OnDisconnected();
        protected abstract void OnError(string error);
        protected virtual void LogNewMessage(ushort type)
        {

        }

        protected virtual void OnUnexpectedDisconnect() { }
        protected virtual void OnReconnecting(int attempt, int maxAttempts) { }
        protected virtual void OnReconnected() { }
        protected virtual void OnReconnectFailed() { }

    }
}