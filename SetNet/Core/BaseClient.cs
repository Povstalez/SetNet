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
        private ClientCommandExecutor _commandExecutor;

        /// <summary>Drives cancellation of the receive and heartbeat loops; replaced on each (re)connect. Guarded by <see cref="_lifecycleLock"/> for swaps/reads. Null before the first connect and after dispose.</summary>
        private CancellationTokenSource? _cancellationTokenSource;

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
        /// Serializes connection-lifecycle transitions: the cancellation-source swap (connect/reconnect) and the
        /// teardown classification, so <see cref="Disconnect"/>, the receive-loop finally, the heartbeat tick, and
        /// <see cref="ReconnectAsync"/> cannot interleave into a torn state or a disposed cancellation source.
        /// </summary>
        private readonly object _lifecycleLock = new object();

        /// <summary>Guarded by <see cref="_lifecycleLock"/>: true once the terminal <see cref="OnDisconnected"/> has fired for the current connection generation; reset on each (re)connect so a future disconnect can fire it again exactly once.</summary>
        private bool _terminalFired;

        /// <summary>Backing field for <see cref="State"/>; <c>volatile</c> so transitions are visible to the heartbeat/send threads without locking.</summary>
        private volatile ConnectionState _state = ConnectionState.Disconnected;

        /// <summary>
        /// The current point in the connection lifecycle. Reflects transitions through Connecting,
        /// Connected, Disconnecting, Reconnecting, and Disconnected, and gates whether sends are allowed.
        /// </summary>
        public ConnectionState State => _state;

        /// <summary>
        /// Initializes the client with its configuration, builds the client-side handler registry via
        /// reflection, and creates the transport connector appropriate to the configured transport type.
        /// No network activity occurs until <see cref="ConnectAsync"/> is called.
        /// </summary>
        /// <param name="config">Connection and behavior settings governing this client.</param>
        protected BaseClient(Configuration config) : base()
        {
            _config = config;
            _commandExecutor = new ClientCommandExecutor();
            _connector = TransportFactory.CreateConnector(config);
            InitDispatchGate(config.MaxInFlightMessages, config.SequentialDispatch);
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

            CancellationToken ct;
            lock (_lifecycleLock)
            {
                _terminalFired = false; // new connection generation: allow one terminal OnDisconnected
                _cancellationTokenSource = new CancellationTokenSource();
                ct = _cancellationTokenSource.Token;
            }
            ResetDispatch(); // re-arm the dispatch gate; a prior Disconnect cancelled the old generation's token

            try
            {
                Connection = await _connector.ConnectAsync(_config, ct).ConfigureAwait(false);
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

            _ = ReceiveLoopAsync(Connection, ct);
            if (!TryCommitConnected(out var prev))
            {
                // A Disconnect()/Dispose() raced the connect tail and already ran the terminal teardown. Do NOT
                // resurrect State to Connected or fire OnConnected after OnDisconnected; just ensure the socket is
                // closed (idempotent) and return.
                Connection?.Close();
                return;
            }
            SafeLifecycleHook(nameof(OnStateChanged), () => OnStateChanged(prev, ConnectionState.Connected));
            SafeLifecycleHook(nameof(OnConnected), OnConnected);
        }

        /// <summary>
        /// Atomically commits the Connected transition under <see cref="_lifecycleLock"/>, but only if no
        /// Disconnect/Dispose intervened during the (awaited) connect. Returns <c>false</c> when an intentional
        /// teardown already ran, so the caller abandons the connection instead of firing a spurious OnConnected
        /// after OnDisconnected and leaving the client falsely Connected over a dead transport.
        /// </summary>
        /// <param name="previous">The state being transitioned from (for <see cref="OnStateChanged"/>), valid only when this returns true.</param>
        /// <returns><c>true</c> if the Connected state was committed; <c>false</c> if a teardown intervened.</returns>
        private bool TryCommitConnected(out ConnectionState previous)
        {
            lock (_lifecycleLock)
            {
                previous = _state;
                if (_disposed || _isIntentionalDisconnect ||
                    previous == ConnectionState.Disconnecting || previous == ConnectionState.Disconnected)
                    return false;
                _state = ConnectionState.Connected;
                return true;
            }
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
            ConnectionState old;
            CancellationTokenSource? cts;
            lock (_lifecycleLock)
            {
                old = _state;
                if (old == ConnectionState.Disconnected || old == ConnectionState.Disconnecting)
                    return; // already torn down (or being torn down) — keep the terminal callback single-fire
                _isIntentionalDisconnect = true;
                _state = ConnectionState.Disconnecting; // flip under the lock so a racing Disconnect bails above
                cts = _cancellationTokenSource;
            }

            SafeLifecycleHook(nameof(OnStateChanged), () => OnStateChanged(old, ConnectionState.Disconnecting));
            try { cts?.Cancel(); } catch (ObjectDisposedException) { /* reconnect disposed it; nothing to cancel */ }
            Connection?.Close();
            ShutdownDispatch();
            FireTerminalDisconnect();
        }

        /// <summary>
        /// Fires the terminal <see cref="OnDisconnected"/> exactly once per connection generation, moving the
        /// state to Disconnected. Guarded by <see cref="_lifecycleLock"/> and <see cref="_terminalFired"/> so the
        /// receive-loop finally, <see cref="Disconnect"/>, and a failed <see cref="ReconnectAsync"/> cannot
        /// double-invoke it.
        /// </summary>
        private void FireTerminalDisconnect()
        {
            lock (_lifecycleLock)
            {
                if (_terminalFired) return;
                _terminalFired = true;
            }
            SetState(ConnectionState.Disconnected);
            SafeLifecycleHook(nameof(OnDisconnected), OnDisconnected);
        }

        /// <summary>
        /// The core inbound pump: continuously receives framed messages and dispatches them until the
        /// connection ends. Its finally block is the single place that classifies how the connection
        /// ended (intentional disconnect, error/heartbeat loss, or graceful server close) and routes to
        /// the appropriate callbacks and optional reconnect.
        /// </summary>
        /// <param name="connection">The transport connection to read from for this loop's lifetime.</param>
        /// <param name="ct">This connection generation's cancellation token, captured at start so a later reconnect's CTS swap cannot redirect this loop.</param>
        /// <returns>A task that completes when the receive loop exits and the disconnect flow has run.</returns>
        /// <remarks>
        /// Runs fire-and-forget. <see cref="OperationCanceledException"/> is swallowed as an intentional
        /// teardown. A null message indicates graceful EOF. Only genuine errors (or heartbeat timeout)
        /// trigger <see cref="OnUnexpectedDisconnect"/> and, when enabled, <see cref="ReconnectAsync"/>.
        /// </remarks>
        private async Task ReceiveLoopAsync(ITransportConnection connection, CancellationToken ct)
        {
            var hadError = false;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
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
                    SafeLifecycleHook(nameof(OnError), () => OnError($"Connection lost: {ex.Message}"));
            }
            finally
            {
                var wasHeartbeat = _isHeartbeatTimeout;
                _isHeartbeatTimeout = false;

                // Classify the teardown atomically so a racing Disconnect() and this finally don't both finalize.
                bool intentional;
                lock (_lifecycleLock)
                {
                    intentional = _isIntentionalDisconnect;
                    if (intentional) _isIntentionalDisconnect = false;
                }

                if (intentional)
                {
                    // Disconnect() owns the terminal callbacks for an intentional teardown.
                }
                else if (hadError || wasHeartbeat)
                {
                    Connection?.Close();
                    ShutdownDispatch();
                    SafeLifecycleHook(nameof(OnUnexpectedDisconnect), OnUnexpectedDisconnect);

                    if (_config.AutoReconnect)
                        _ = ReconnectAsync();
                    else
                        FireTerminalDisconnect();
                }
                else
                {
                    Connection?.Close();
                    ShutdownDispatch();
                    FireTerminalDisconnect();
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
                SafeLifecycleHook(nameof(OnError), () => OnError("Heartbeat timeout - no response from server."));
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
                // Abort if the user disconnected (or disposed) while we were reconnecting, so we don't keep
                // retrying — or reconnect — a connection the application explicitly tore down.
                if (_disposed || _isIntentionalDisconnect) { FireTerminalDisconnect(); return; }

                SafeLifecycleHook(nameof(OnReconnecting), () => OnReconnecting(attempt, _config.MaxReconnectAttempts));
                await Task.Delay(_config.ReconnectDelayMs).ConfigureAwait(false);

                if (_disposed || _isIntentionalDisconnect) { FireTerminalDisconnect(); return; }

                try
                {
                    // Re-check intent and swap the receive-loop cancellation source ATOMICALLY under the lock, so
                    // a Disconnect()/Dispose() that lands in this window is honoured instead of being silently
                    // overwritten (which would resurrect a connection the application explicitly tore down).
                    CancellationToken ct = default;
                    bool bail = false;
                    lock (_lifecycleLock)
                    {
                        if (_disposed || _isIntentionalDisconnect)
                        {
                            bail = true;
                        }
                        else
                        {
                            // Publish the new source before disposing the old one so Disconnect()/Dispose() never
                            // read a half-swapped or disposed reference; if a Disconnect now runs it cancels THIS
                            // new source, which aborts the connect below.
                            var oldCts = _cancellationTokenSource;
                            _cancellationTokenSource = new CancellationTokenSource();
                            ct = _cancellationTokenSource.Token;
                            try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }
                            oldCts?.Dispose();
                        }
                    }
                    if (bail) { FireTerminalDisconnect(); return; }

                    Connection = await _connector.ConnectAsync(_config, ct).ConfigureAwait(false);

                    if (_config.HeartbeatEnabled)
                    {
                        // The shared-scheduler heartbeat tick resumes automatically once State is Connected.
                        Interlocked.Exchange(ref _lastPongReceivedTicks, MonotonicClock.Timestamp);
                    }

                    lock (_lifecycleLock) { _terminalFired = false; } // new generation: re-arm the terminal guard
                    ResetDispatch(); // re-arm the dispatch gate; the prior teardown cancelled the old generation's token
                    _ = ReceiveLoopAsync(Connection, ct);
                    if (!TryCommitConnected(out var prev))
                    {
                        // A Disconnect()/Dispose() raced the reconnect tail; it already ran teardown. Don't fire a
                        // spurious OnReconnected or resurrect Connected over a dead transport.
                        Connection?.Close();
                        return;
                    }
                    SafeLifecycleHook(nameof(OnStateChanged), () => OnStateChanged(prev, ConnectionState.Connected));
                    SafeLifecycleHook(nameof(OnReconnected), OnReconnected);
                    return;
                }
                catch (Exception ex)
                {
                    _config.Logger.Log(
                        $"Reconnect attempt {attempt}/{_config.MaxReconnectAttempts} failed: {ex.Message}",
                        global::SetNet.Logging.LogLevel.Warning);
                }
            }

            SafeLifecycleHook(nameof(OnReconnectFailed), OnReconnectFailed);
            FireTerminalDisconnect();
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
        protected Task SendAsync<T>(ushort type, T message, DeliveryMethod delivery, byte channel)
            => SendRawAsync(type, SetNetSerializer.Serialize(message), delivery, channel);

        /// <summary>
        /// Sends an already-serialized payload to the server using the configured default delivery, <b>without
        /// serializing</b>. The escape hatch for forwarding raw bytes received in <see cref="BaseSocket.OnRawFrame"/>
        /// (relay/proxy scenarios), avoiding a needless deserialize-then-reserialize round trip.
        /// </summary>
        /// <param name="type">The wire type id to frame the payload under.</param>
        /// <param name="payload">The raw, already-serialized message body.</param>
        /// <returns>A task that completes once the frame has been handed to the transport.</returns>
        protected Task SendRawAsync(ushort type, byte[] payload)
            => SendRawAsync(type, payload, _config.DefaultDelivery, 0);

        /// <summary>
        /// Sends an already-serialized payload with an explicit delivery method and reliable-UDP channel, without
        /// serializing. See <see cref="SendRawAsync(ushort, byte[])"/>.
        /// </summary>
        /// <param name="type">The wire type id to frame the payload under.</param>
        /// <param name="payload">The raw, already-serialized message body.</param>
        /// <param name="delivery">The delivery guarantee for this send.</param>
        /// <param name="channel">Reliable-UDP channel id (ignored for TCP/unreliable).</param>
        /// <returns>A task that completes once the frame has been handed to the transport.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the client has been disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not currently in the Connected state.</exception>
        protected async Task SendRawAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BaseClient));
            var conn = Connection;
            if (State != ConnectionState.Connected || conn == null)
                throw new InvalidOperationException($"Cannot send: state is '{State}'.");

            _config.Metrics.IncrementMessagesSent();
            await conn.SendAsync(type, payload, delivery, channel).ConfigureAwait(false);
        }

        /// <summary>
        /// Flushes buffered sends when <see cref="Configuration.SendBatching"/> is enabled (a no-op otherwise).
        /// Call after composing a tick's messages to write them in one operation.
        /// </summary>
        /// <returns>A task that completes once buffered data has been written.</returns>
        protected Task FlushAsync() => Connection?.FlushAsync() ?? Task.CompletedTask;

        /// <summary>
        /// Binds every reflection-discovered client handler to its wire type id on the message processor.
        /// Called on connect so inbound frames are dispatched to the correct <see cref="IClientMessageHandler{TMessage}"/>.
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
            return data => _commandExecutor.DispatchAsync(messageType, data);
        }

        /// <summary>
        /// Transitions the connection state and notifies via <see cref="OnStateChanged"/> when it actually
        /// changes. Centralizing transitions here keeps the public <see cref="State"/> and the change
        /// notifications consistent.
        /// </summary>
        /// <param name="newState">The state to move into; a no-op if it equals the current state.</param>
        private void SetState(ConnectionState newState)
        {
            var old = _state;
            if (old == newState) return;
            _state = newState;
            SafeLifecycleHook(nameof(OnStateChanged), () => OnStateChanged(old, newState));
        }

        /// <summary>Runs an application lifecycle hook without letting user code interrupt connection cleanup.</summary>
        private void SafeLifecycleHook(string hookName, Action hook)
        {
            try { hook(); }
            catch (Exception ex)
            {
                SafeLog($"{hookName} hook failed: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
            }
        }

        /// <summary>Logs best-effort; a throwing application logger must not escape lifecycle cleanup.</summary>
        private void SafeLog(string message, global::SetNet.Logging.LogLevel level)
        {
            try { _config.Logger.Log(message, level); }
            catch { }
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
            DisposeDispatch();
            lock (_lifecycleLock)
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
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
