using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Commands;
using SetNet.Data;

namespace SetNet.Core
{
    public abstract class BaseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<Guid, BasePeer> _clients = new Dictionary<Guid, BasePeer>();
        private readonly Configuration _config;
        private readonly CommandExecutor<IServerMessageHandler> _commandExecutor;
        private CancellationTokenSource _cts;
        private bool _disposed;

        protected BaseServer(Configuration config)
        {
            _config = config;
            _commandExecutor = new CommandExecutor<IServerMessageHandler>();
            _listener = new TcpListener(IPAddress.Parse(config.Host), config.Port);
        }

        public async Task StartAsync()
        {
            if (_cts != null)
                throw new InvalidOperationException("Server is already started or starting.");

            _cts = new CancellationTokenSource();
            _listener.Start();
            _cts.Token.Register(() => _listener.Stop());

            Console.WriteLine($"Server started on {_config.Host}:{_config.Port}");

            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (_cts.IsCancellationRequested) { break; }

                var peerInfo = new PeerInfo(client, _config, this, _commandExecutor);
                var peer = OnNewClient(peerInfo);

                lock (_clients)
                {
                    _clients[peerInfo.Id] = peer;
                }

                Console.WriteLine($"Client connected: {peerInfo.Id}");
            }
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            lock (_clients)
            {
                foreach (var client in _clients.Values)
                    client.Close();

                _clients.Clear();
            }

            Console.WriteLine("Server stopped");
            return Task.CompletedTask;
        }

        public void RemoveClient(PeerInfo peerInfo)
        {
            lock (_clients)
            {
                _clients.Remove(peerInfo.Id);
            }
        }

        protected abstract BasePeer OnNewClient(PeerInfo peerInfo);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;

            _cts?.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients.Values)
                    client.Close();
                _clients.Clear();
            }
            _cts?.Dispose();
        }
    }
}