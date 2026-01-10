using System;
using System.Net.Sockets;
using SetNet.Core;
using SetNet.Core.Commands;
using SetNet.Data;

namespace SetNet.Config
{
    public class PeerInfo
    {
        public TcpClient Client;
        public Guid Id;
        public Configuration Config;

        private BaseServer _server;
        public readonly CommandExecutor<IServerMessageHandler> CommandExecutor;

        public PeerInfo(TcpClient client, Configuration config, BaseServer server, CommandExecutor<IServerMessageHandler> commandExecutor)
        {
            Client = client;
            Config = config;
            _server = server;
            CommandExecutor = commandExecutor;
            Id = Guid.NewGuid();
        }

        public void Disconnect()
        {
            Client.Close();
            _server?.RemoveClient(this);
        }
    }
}