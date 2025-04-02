using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using SetNet.Messaging;

namespace SetNet.Core
{
    public class BaseSocket
    {
        protected NetworkStream Stream;
        protected readonly PacketBuilder PacketBuilder;

        private readonly MessageProcessor _messageProcessor;

        public BaseSocket()
        {
            _messageProcessor = new MessageProcessor();
            PacketBuilder = new PacketBuilder();
        }

        protected void RegisterDataHandler(ushort type, Func<byte[], Task> handler)
        {
            _messageProcessor.RegisterHandler(type, handler);
        }
        
        protected void RegisterDataHandler(ushort type, Action<byte[]> handler)
        {
            _messageProcessor.RegisterHandler(type, handler);
        }

        protected void HandleMessage(ushort type, byte[] data)
        {
            _messageProcessor.ProcessMessage(type, data);
        }
    }
}