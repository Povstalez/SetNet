using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Messaging;

namespace SetNet.Core
{

    public abstract class BaseClient : BaseSocket
    {
        private readonly Configuration _config;
        private TcpClient _client;

        private CancellationTokenSource _cancellationTokenSource;

        protected BaseClient(Configuration config) : base()
        {
            _config = config;
        }

        public async Task ConnectAsync()
        {
            RegisterDataHandlers();

            _cancellationTokenSource = new CancellationTokenSource();

            _client = new TcpClient();
            await _client.ConnectAsync(_config.Host, _config.Port);
            Stream = _client.GetStream();

            _ = ReceiveAsync(_client);
        }

        public void Disconnect()
        {
            if(_cancellationTokenSource.Token.IsCancellationRequested)
                return;
            
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
                OnError("Connection lost due to IO error.");
            }
            catch (SocketException)
            {
                OnError("Connection lost due to socket error.");
            }
            catch (Exception ex)
            {
                OnError($"Error: {ex.Message}");
            }
            finally
            {
                Disconnect();
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

        protected abstract void RegisterDataHandlers();
        protected abstract void OnDisconnected();
        protected abstract void OnError(string error);
        protected virtual void LogNewMessage(ushort type)
        {
            
        }

    }
}