using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Commands;
using SetNet.Data;

namespace SetNet.Core
{
    public abstract class BaseServer
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<Guid, BasePeer> _clients = new Dictionary<Guid, BasePeer>();
        private readonly Configuration _config;
        private readonly CommandExecutor<IServerMessageHandler> _commandExecutor;
        private bool _running;

        protected BaseServer(Configuration config)
        {
            _config = config;
            _commandExecutor = new CommandExecutor<IServerMessageHandler>();
            _listener = new TcpListener(IPAddress.Parse(config.Host), config.Port);
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _running = true;

            Console.WriteLine($"Server started on {_config.Host}:{_config.Port}");

            while (_running)
            {
                var client = await _listener.AcceptTcpClientAsync();
                var peerInfo = new PeerInfo(client, _config, this, _commandExecutor);
                var peer = OnNewClient(peerInfo);

                lock (_clients)
                {
                    _clients[peerInfo.Id] = peer;
                }

                Console.WriteLine($"Client connected: {peerInfo.Id}");
            }
        }

        public async Task StopAsync()
        {
            _running = false;
            _listener.Stop();

            lock (_clients)
            {
                foreach (var client in _clients.Values)
                    client.Close();

                _clients.Clear();
            }

            Console.WriteLine("Server stopped");
        }

        public void RemoveClient(PeerInfo peerInfo)
        {
            lock (_clients)
            {
                _clients.Remove(peerInfo.Id);
            }
        }

        protected abstract BasePeer OnNewClient(PeerInfo peerInfo);
    }
}