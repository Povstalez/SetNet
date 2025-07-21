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
                Console.WriteLine($"Client {CurrentPeerInfo.Id} disconnected due to IO error.");
            }
            catch (SocketException)
            {
                Console.WriteLine($"Client {CurrentPeerInfo.Id} disconnected due to socket error.");
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine($"Client {CurrentPeerInfo.Id} connection was closed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client {CurrentPeerInfo.Id} error: {ex.Message}");
            }
            finally
            {
                Close();
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
            CurrentPeerInfo.Disconnect();
            OnDisconnected();
        }

        protected abstract void RegisterDataHandlers();
        protected abstract void OnDisconnected();

    }
}