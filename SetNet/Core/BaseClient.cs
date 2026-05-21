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
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private volatile bool _isIntentionalDisconnect;
        private volatile bool _isHeartbeatTimeout;
        private long _lastPongReceivedTicks;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        protected BaseClient(Configuration config) : base()
        {
            _config = config;
            _commandExecutor = new CommandExecutor<IClientMessageHandler>();
        }

        public async Task ConnectAsync()
        {
            SetState(ConnectionState.Connecting);
            _isIntentionalDisconnect = false;
            RegisterDataHandlers();

            _cancellationTokenSource = new CancellationTokenSource();
            _client = new TcpClient();

            await ConnectWithTimeoutAsync();
            Stream = _client.GetStream();

            if (_config.HeartbeatEnabled)
            {
                Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);
                RegisterDataHandler(SystemMessageTypes.Pong, OnPongReceived);
                _ = HeartbeatLoopAsync(_cancellationTokenSource.Token);
            }

            _ = ReceiveAsync(_client);
            SetState(ConnectionState.Connected);
            OnConnected();
        }

        public void Disconnect()
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
                return;

            SetState(ConnectionState.Disconnecting);
            _isIntentionalDisconnect = true;
            _cancellationTokenSource.Cancel();
            _client?.Close();
            SetState(ConnectionState.Disconnected);
            OnDisconnected();
        }

        private async Task ConnectWithTimeoutAsync()
        {
            var connectTask = _client.ConnectAsync(_config.Host, _config.Port);

            if (_config.ConnectTimeoutMs > 0)
            {
                if (await Task.WhenAny(connectTask, Task.Delay(_config.ConnectTimeoutMs)) != connectTask)
                {
                    _client.Close();
                    SetState(ConnectionState.Disconnected);
                    throw new TimeoutException($"Connection timed out after {_config.ConnectTimeoutMs}ms");
                }
            }

            await connectTask;
        }

        private async Task ReceiveAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[_config.BufferSize];
            var packetBuilder = new PacketBuilder();
            var hadError = false;

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
                hadError = true;
                if (!_isIntentionalDisconnect && !_isHeartbeatTimeout)
                    OnError("Connection lost due to IO error.");
            }
            catch (SocketException)
            {
                hadError = true;
                if (!_isIntentionalDisconnect && !_isHeartbeatTimeout)
                    OnError("Connection lost due to socket error.");
            }
            catch (Exception ex)
            {
                hadError = true;
                if (!_isIntentionalDisconnect && !_isHeartbeatTimeout)
                    OnError($"Error: {ex.Message}");
            }
            finally
            {
                var wasHeartbeat = _isHeartbeatTimeout;
                _isHeartbeatTimeout = false;

                if (_isIntentionalDisconnect)
                {
                    _isIntentionalDisconnect = false;
                }
                else if (hadError || wasHeartbeat)
                {
                    _client?.Close();
                    OnUnexpectedDisconnect();

                    if (_config.AutoReconnect)
                        _ = ReconnectAsync();
                    else
                    {
                        SetState(ConnectionState.Disconnected);
                        OnDisconnected();
                    }
                }
                else
                {
                    _client?.Close();
                    SetState(ConnectionState.Disconnected);
                    OnDisconnected();
                }
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(_config.HeartbeatIntervalMs, token);

                    var elapsed = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPongReceivedTicks))
                                  / TimeSpan.TicksPerMillisecond;

                    if (elapsed > _config.HeartbeatTimeoutMs)
                    {
                        _isHeartbeatTimeout = true;
                        OnError("Heartbeat timeout - no response from server.");
                        _client?.Close();
                        return;
                    }

                    var packet = PacketBuilder.BuildPacket(SystemMessageTypes.Ping, Array.Empty<byte>());
                    await SendAsync(packet, token);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task ReconnectAsync()
        {
            SetState(ConnectionState.Reconnecting);

            for (int attempt = 1; attempt <= _config.MaxReconnectAttempts; attempt++)
            {
                OnReconnecting(attempt, _config.MaxReconnectAttempts);
                await Task.Delay(_config.ReconnectDelayMs);

                try
                {
                    _isIntentionalDisconnect = false;

                    // Cancel old CTS to stop any stale HeartbeatLoopAsync before reconnecting
                    var oldCts = _cancellationTokenSource;
                    _cancellationTokenSource = new CancellationTokenSource();
                    oldCts.Cancel();
                    oldCts.Dispose();

                    _client = new TcpClient();
                    await ConnectWithTimeoutAsync();
                    Stream = _client.GetStream();

                    if (_config.HeartbeatEnabled)
                    {
                        Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);
                        _ = HeartbeatLoopAsync(_cancellationTokenSource.Token);
                    }

                    _ = ReceiveAsync(_client);
                    SetState(ConnectionState.Connected);
                    OnReconnected();
                    return;
                }
                catch { }
            }

            OnReconnectFailed();
            SetState(ConnectionState.Disconnected);
            OnDisconnected();
        }

        private void OnPongReceived(byte[] data)
        {
            Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);
        }

        protected async Task SendAsync<T>(ushort type, T message)
        {
            var data = MessagePackSerializer.Serialize(message);
            var packet = PacketBuilder.BuildPacket(type, data);
            await SendAsync(packet);
        }

        private async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await Stream.WriteAsync(data, 0, data.Length);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected virtual void RegisterDataHandlers()
        {
            foreach (var messageType in _commandExecutor.Keys)
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
        }

        private Func<byte[], Task> CreateHandlerDelegate(ushort messageType)
        {
            return async data => await _commandExecutor.Handlers[messageType].HandleAsync(data);
        }

        private void SetState(ConnectionState newState)
        {
            var old = State;
            if (old == newState) return;
            State = newState;
            OnStateChanged(old, newState);
        }

        protected abstract void OnConnected();
        protected abstract void OnDisconnected();
        protected abstract void OnError(string error);

        protected virtual void LogNewMessage(ushort type) { }
        protected virtual void OnUnexpectedDisconnect() { }
        protected virtual void OnReconnecting(int attempt, int maxAttempts) { }
        protected virtual void OnReconnected() { }
        protected virtual void OnReconnectFailed() { }
        protected virtual void OnStateChanged(ConnectionState from, ConnectionState to) { }
    }
}