using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Commands;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Messaging;

namespace SetNet.Core
{
    /// <summary>
    /// Abstract client endpoint that connects to a <see cref="BaseServer"/>, runs the inbound receive loop,
    /// and manages the full connection lifecycle (connect, heartbeat, unexpected-loss detection, optional
    /// auto-reconnect, and disposal). Concrete games/applications subclass this and override the protected
    /// lifecycle hooks (<see cref="OnConnected"/>, <see cref="OnDisconnected"/>, <see cref="OnError"/>, and
    /// the reconnect callbacks) to react to state transitions. It sits on the client side of the
    /// client/server split, mirroring <see cref="BasePeer"/> on the server.
    /// </summary>
    public abstract class BaseClient : BaseSocket, IDisposable
    {
        /// <summary>Connection and behavior settings (host, port, heartbeat, reconnect policy, delivery, logger).</summary>
        private readonly Configuration _config;

        /// <summary>Transport-specific connector used to establish a new <see cref="ITransportConnection"/> on connect/reconnect.</summary>
        private readonly ITransportConnector _connector;

        /// <summary>Reflection-discovered registry of client-side message handlers, keyed by wire type id.</summary>
        private CommandExecutor<IClientMessageHandler> _commandExecutor;

        /// <summary>Drives cancellation of the receive and heartbeat loops; replaced on each (re)connect.</summary>
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>True while a caller-initiated <see cref="Disconnect"/> is in progress, so the receive loop treats the teardown as intentional and skips error/reconnect handling.</summary>
        private volatile bool _isIntentionalDisconnect;

        /// <summary>True when the connection is being torn down because the heartbeat timed out, so the loss is treated as unexpected without double-reporting an error.</summary>
        private volatile bool _isHeartbeatTimeout;

        /// <summary>Monotonic timestamp (ticks) of the last received Pong, used by the heartbeat loop to detect a stalled server.</summary>
        private long _lastPongReceivedTicks;

        /// <summary>Registration id of the heartbeat tick on the shared scheduler (scheduled once for the client's lifetime).</summary>
        private long _heartbeatTickId;

        /// <summary>True once the heartbeat tick has been registered, so it isn't double-registered across reconnects.</summary>
        private bool _heartbeatScheduled;

        /// <summary>Guards against use-after-dispose and double-dispose.</summary>
        private bool _disposed;

        /// <summary>
        /// The current point in the connection lifecycle. Reflects transitions through Connecting,
        /// Connected, Disconnecting, Reconnecting, and Disconnected, and gates whether sends are allowed.
        /// </summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>
        /// Initializes the client with its configuration, builds the client-side handler registry via
        /// reflection, and creates the transport connector appropriate to the configured transport type.
        /// No network activity occurs until <see cref="ConnectAsync"/> is called.
        /// </summary>
        /// <param name="config">Connection and behavior settings governing this client.</param>
        protected BaseClient(Configuration config) : base()
        {
            _config = config;
            _commandExecutor = new CommandExecutor<IClientMessageHandler>();
            _connector = TransportFactory.CreateConnector(config);
            InitDispatchGate(config.MaxInFlightMessages);
        }

        /// <summary>
        /// Establishes the connection to the server, registers message handlers, and starts the receive
        /// loop (plus the heartbeat loop when enabled). This is the entry point that brings the client
        /// online; on success the client is in the Connected state and <see cref="OnConnected"/> has fired.
        /// </summary>
        /// <returns>A task that completes once the connection is established and the loops are running.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the client has already been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not currently in the Disconnected state.</exception>
        /// <remarks>
        /// Guards against concurrent/duplicate connects via the state check. If the transport handshake
        /// fails, the state is rolled back to Disconnected and the originating exception is rethrown.
        /// The receive and heartbeat loops are started fire-and-forget and observed via cancellation.
        /// </remarks>
        public async Task ConnectAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BaseClient));
            if (State != ConnectionState.Disconnected)
                throw new InvalidOperationException($"Cannot connect: state is '{State}'.");
            _config.Validate();

            SetState(ConnectionState.Connecting);
            _isIntentionalDisconnect = false;
            RegisterDataHandlers();

            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                Connection = await _connector.ConnectAsync(_config, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch
            {
                SetState(ConnectionState.Disconnected);
                throw;
            }

            if (_config.HeartbeatEnabled)
            {
                Interlocked.Exchange(ref _lastPongReceivedTicks, MonotonicClock.Timestamp);
                RegisterDataHandler(SystemMessageTypes.Pong, OnPongReceived);
                if (!_heartbeatScheduled)
                {
                    _heartbeatScheduled = true;
                    _heartbeatTickId = TimerScheduler.Shared.Schedule(_config.HeartbeatIntervalMs, HeartbeatTick);
                }
            }

            _ = ReceiveLoopAsync(Connection);
            SetState(ConnectionState.Connected);
            OnConnected();
        }

        /// <summary>
        /// Performs a caller-initiated, graceful shutdown of the connection. Marks the teardown as
        /// intentional so the receive loop does not treat it as an error or trigger auto-reconnect, then
        /// cancels the loops, closes the transport, and fires <see cref="OnDisconnected"/>.
        /// </summary>
        /// <remarks>
        /// Idempotent: returns immediately if there is no active connection or cancellation has already
        /// been requested. Setting <see cref="_isIntentionalDisconnect"/> before cancelling is what
        /// distinguishes this from an unexpected loss in the receive loop's finally block.
        /// </remarks>
        public void Disconnect()
        {
            if (_cancellationTokenSource == null || _cancellationTokenSource.Token.IsCancellationRequested)
                return;

            SetState(ConnectionState.Disconnecting);
            _isIntentionalDisconnect = true;
            _cancellationTokenSource.Cancel();
            Connection?.Close();
            SetState(ConnectionState.Disconnected);
            OnDisconnected();
        }

        /// <summary>
        /// The core inbound pump: continuously receives framed messages and dispatches them until the
        /// connection ends. Its finally block is the single place that classifies how the connection
        /// ended (intentional disconnect, error/heartbeat loss, or graceful server close) and routes to
        /// the appropriate callbacks and optional reconnect.
        /// </summary>
        /// <param name="connection">The transport connection to read from for this loop's lifetime.</param>
        /// <returns>A task that completes when the receive loop exits and the disconnect flow has run.</returns>
        /// <remarks>
        /// Runs fire-and-forget. <see cref="OperationCanceledException"/> is swallowed as an intentional
        /// teardown. A null message indicates graceful EOF. Only genuine errors (or heartbeat timeout)
        /// trigger <see cref="OnUnexpectedDisconnect"/> and, when enabled, <see cref="ReconnectAsync"/>.
        /// </remarks>
        private async Task ReceiveLoopAsync(ITransportConnection connection)
        {
            var hadError = false;

            try
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var message = await connection.ReceiveAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                    if (message == null) break; // graceful close / EOF
                    var m = message.Value;

                    _config.Metrics.IncrementMessagesReceived();
                    LogNewMessage(m.Type);
                    await DispatchAsync(m.Type, m.Payload).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is treated as an intentional teardown.
            }
            catch (Exception ex)
            {
                hadError = true;
                if (!_isIntentionalDisconnect && !_isHeartbeatTimeout)
                    OnError($"Connection lost: {ex.Message}");
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
                    Connection?.Close();
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
                    Connection?.Close();
                    SetState(ConnectionState.Disconnected);
                    OnDisconnected();
                }
            }
        }

        /// <summary>
        /// Heartbeat tick driven by the shared <see cref="TimerScheduler"/>. While the client is Connected it sends a
        /// Ping and detects a silently dead server (no FIN, no error) by checking the time since the last Pong, then
        /// closes the connection so the receive loop classifies the loss as unexpected. A no-op while not Connected
        /// (e.g. mid-reconnect), so a single registration serves the client's whole lifetime including reconnects.
        /// </summary>
        private void HeartbeatTick()
        {
            if (State != ConnectionState.Connected) return;

            var elapsed = MonotonicClock.ElapsedMs(Interlocked.Read(ref _lastPongReceivedTicks));
            if (elapsed > _config.HeartbeatTimeoutMs)
            {
                _isHeartbeatTimeout = true;
                OnError("Heartbeat timeout - no response from server.");
                Connection?.Close();
                return;
            }

            var conn = Connection;
            if (conn != null)
                _ = conn.SendAsync(SystemMessageTypes.Ping, Array.Empty<byte>(), DeliveryMethod.Unreliable);
        }

        /// <summary>
        /// Attempts to re-establish a lost connection up to <see cref="Configuration.MaxReconnectAttempts"/>
        /// times, with a configurable delay between attempts, restarting the receive and heartbeat loops on
        /// success. Invoked from the receive loop only when auto-reconnect is enabled and the loss was
        /// unexpected.
        /// </summary>
        /// <returns>A task that completes when reconnect succeeds or all attempts are exhausted.</returns>
        /// <remarks>
        /// Fires <see cref="OnReconnecting"/> before each attempt; on success fires <see cref="OnReconnected"/>
        /// and returns; if every attempt fails it fires <see cref="OnReconnectFailed"/> followed by
        /// <see cref="OnDisconnected"/>. A fresh <see cref="CancellationTokenSource"/> is installed and the
        /// previous one cancelled/disposed on each attempt to ensure no stale heartbeat loop survives.
        /// </remarks>
        private async Task ReconnectAsync()
        {
            SetState(ConnectionState.Reconnecting);

            for (int attempt = 1; attempt <= _config.MaxReconnectAttempts; attempt++)
            {
                OnReconnecting(attempt, _config.MaxReconnectAttempts);
                await Task.Delay(_config.ReconnectDelayMs).ConfigureAwait(false);

                try
                {
                    _isIntentionalDisconnect = false;

                    // Replace the receive-loop cancellation source for the new connection generation.
                    var oldCts = _cancellationTokenSource;
                    _cancellationTokenSource = new CancellationTokenSource();
                    oldCts.Cancel();
                    oldCts.Dispose();

                    Connection = await _connector.ConnectAsync(_config, _cancellationTokenSource.Token).ConfigureAwait(false);

                    if (_config.HeartbeatEnabled)
                    {
                        // The shared-scheduler heartbeat tick resumes automatically once State is Connected.
                        Interlocked.Exchange(ref _lastPongReceivedTicks, MonotonicClock.Timestamp);
                    }

                    _ = ReceiveLoopAsync(Connection);
                    SetState(ConnectionState.Connected);
                    OnReconnected();
                    return;
                }
                catch (Exception ex)
                {
                    _config.Logger.Log(
                        $"Reconnect attempt {attempt}/{_config.MaxReconnectAttempts} failed: {ex.Message}",
                        global::SetNet.Logging.LogLevel.Warning);
                }
            }

            OnReconnectFailed();
            SetState(ConnectionState.Disconnected);
            OnDisconnected();
        }

        /// <summary>
        /// Heartbeat handler for inbound Pong frames: records the arrival time so the heartbeat loop can
        /// confirm the server is still responsive and reset its timeout window.
        /// </summary>
        /// <param name="data">The Pong payload (empty by protocol; ignored).</param>
        private void OnPongReceived(byte[] data)
        {
            Interlocked.Exchange(ref _lastPongReceivedTicks, MonotonicClock.Timestamp);
        }

        /// <summary>
        /// Serializes and sends a strongly-typed message to the server using the configured default
        /// delivery method. The primary convenience send path for application messages.
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        protected Task SendAsync<T>(ushort type, T message)
            => SendAsync(type, message, _config.DefaultDelivery);

        /// <summary>
        /// Serializes and sends a strongly-typed message to the server using an explicit delivery method,
        /// allowing per-call control over reliable vs. unreliable transmission.
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <param name="delivery">The delivery guarantee (e.g. Reliable or Unreliable) to use for this send.</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        protected Task SendAsync<T>(ushort type, T message, DeliveryMethod delivery)
            => SendAsync(type, message, delivery, 0);

        /// <summary>
        /// Serializes and sends a message with explicit delivery method and reliable-UDP channel. Use distinct
        /// channels for independent reliable streams so a loss on one (e.g. chat) doesn't delay another (e.g. movement).
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <param name="delivery">The delivery guarantee for this send.</param>
        /// <param name="channel">Reliable-UDP channel id (0-based; must be &lt; <see cref="Configuration.UdpReliableChannels"/>). Ignored for TCP/unreliable.</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the client has been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not currently in the Connected state.</exception>
        protected async Task SendAsync<T>(ushort type, T message, DeliveryMethod delivery, byte channel)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BaseClient));
            var conn = Connection;
            if (State != ConnectionState.Connected || conn == null)
                throw new InvalidOperationException($"Cannot send: state is '{State}'.");

            var data = MessagePackSerializer.Serialize(message);
            _config.Metrics.IncrementMessagesSent();
            await conn.SendAsync(type, data, delivery, channel).ConfigureAwait(false);
        }

        /// <summary>
        /// Flushes buffered sends when <see cref="Configuration.SendBatching"/> is enabled (a no-op otherwise).
        /// Call after composing a tick's messages to write them in one operation.
        /// </summary>
        /// <returns>A task that completes once buffered data has been written.</returns>
        protected Task FlushAsync() => Connection?.FlushAsync() ?? Task.CompletedTask;

        /// <summary>
        /// Binds every reflection-discovered client handler to its wire type id on the message processor.
        /// Called on connect so inbound frames are dispatched to the correct <see cref="IClientMessageHandler"/>.
        /// </summary>
        /// <remarks>Virtual so subclasses can extend or replace the default registration behavior.</remarks>
        protected virtual void RegisterDataHandlers()
        {
            foreach (var messageType in _commandExecutor.Keys)
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
        }

        /// <summary>
        /// Builds the async dispatch delegate for a given message type that forwards the raw payload to the
        /// registered handler instance. Factored out so the captured message type is bound correctly per handler.
        /// </summary>
        /// <param name="messageType">The wire type id whose handler the delegate should invoke.</param>
        /// <returns>A delegate that asynchronously routes payload bytes to the matching client handler.</returns>
        private Func<byte[], Task> CreateHandlerDelegate(ushort messageType)
        {
            return async data => await _commandExecutor.Handlers[messageType].HandleAsync(data).ConfigureAwait(false);
        }

        /// <summary>
        /// Transitions the connection state and notifies via <see cref="OnStateChanged"/> when it actually
        /// changes. Centralizing transitions here keeps the public <see cref="State"/> and the change
        /// notifications consistent.
        /// </summary>
        /// <param name="newState">The state to move into; a no-op if it equals the current state.</param>
        private void SetState(ConnectionState newState)
        {
            var old = State;
            if (old == newState) return;
            State = newState;
            OnStateChanged(old, newState);
        }

        /// <summary>
        /// Releases all resources held by the client, disconnecting if still connected. Implements
        /// <see cref="IDisposable"/> and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Core dispose logic following the standard dispose pattern: tears down the connection and disposes
        /// the transport and cancellation source. Subclasses may override to release additional resources.
        /// </summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/> (managed resources should be released); false from a finalizer.</param>
        /// <remarks>Idempotent via the <see cref="_disposed"/> guard.</remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;

            Disconnect();
            if (_heartbeatScheduled) TimerScheduler.Shared.Unschedule(_heartbeatTickId);
            Connection?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        /// <summary>
        /// Logs a handler exception through the configured logger, overriding the base no-op so client-side
        /// handler failures are visible rather than silently swallowed.
        /// </summary>
        /// <param name="type">The wire type id of the message whose handler threw.</param>
        /// <param name="exception">The exception raised by the failing handler.</param>
        protected override void HandleProcessingError(ushort type, Exception exception)
        {
            _config.Logger.Log($"Client message handler for type {type} failed: {exception.Message}",
                global::SetNet.Logging.LogLevel.Error);
        }

        /// <summary>Lifecycle hook invoked once the connection is established and the receive loop is running.</summary>
        protected abstract void OnConnected();

        /// <summary>Lifecycle hook invoked when the connection has fully closed (after an intentional disconnect, a failed reconnect, or a graceful server close).</summary>
        protected abstract void OnDisconnected();

        /// <summary>Lifecycle hook invoked on an unexpected error (network failure, server crash, or heartbeat timeout) with a human-readable description.</summary>
        /// <param name="error">A message describing the error that occurred.</param>
        protected abstract void OnError(string error);

        /// <summary>Hook invoked for every inbound message; override to trace or count received frames by type.</summary>
        /// <param name="type">The wire type id of the message just received.</param>
        protected virtual void LogNewMessage(ushort type) { }

        /// <summary>Hook invoked when the server drops the connection unexpectedly (error or heartbeat timeout), before any reconnect attempt.</summary>
        protected virtual void OnUnexpectedDisconnect() { }

        /// <summary>Hook invoked before each auto-reconnect attempt so progress can be surfaced to the user.</summary>
        /// <param name="attempt">The 1-based index of the current reconnect attempt.</param>
        /// <param name="maxAttempts">The total number of attempts that will be made before giving up.</param>
        protected virtual void OnReconnecting(int attempt, int maxAttempts) { }

        /// <summary>Hook invoked when an auto-reconnect attempt succeeds and the connection is live again.</summary>
        protected virtual void OnReconnected() { }

        /// <summary>Hook invoked when all auto-reconnect attempts have been exhausted without success, just before <see cref="OnDisconnected"/>.</summary>
        protected virtual void OnReconnectFailed() { }

        /// <summary>Hook invoked on every connection-state transition so subclasses can react to lifecycle changes uniformly.</summary>
        /// <param name="from">The state being left.</param>
        /// <param name="to">The state being entered.</param>
        protected virtual void OnStateChanged(ConnectionState from, ConnectionState to) { }
    }
}
