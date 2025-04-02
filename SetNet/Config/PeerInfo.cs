using System;
using System.Net.Sockets;
using SetNet.Core;

namespace SetNet.Config
{
    public class PeerInfo
    {
        public TcpClient Client;
        public Guid Id;
        public Configuration Config;

        private BaseServer _server;

        public PeerInfo(TcpClient client, Configuration config, BaseServer server)
        {
            Client = client;
            Config = config;
            _server = server;
            Id = Guid.NewGuid();
        }

        public void Disconnect()
        {
            Client.Close();
            _server?.RemoveClient(this);
        }
    }
}