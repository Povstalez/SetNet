using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Core
{
    /// <summary>
    /// Abstract server-side representation of a single connected client. Each accepted connection is paired
    /// with one peer (created by <see cref="BaseServer.OnNewClient"/>) that runs the per-client receive
    /// loop, monitors the heartbeat, and sends responses. It is the server-side counterpart to
    /// <see cref="BaseClient"/> and, like it, distinguishes intentional closes from unexpected client loss.
    /// In Both mode a peer may own a secondary UDP channel in addition to its primary connection.
    /// </summary>
    public abstract class BasePeer : BaseSocket
    {
        /// <summary>Connection handle and metadata (id, config, owning server, handler registry) for this client.</summary>
        protected readonly PeerInfo CurrentPeerInfo;

        /// <summary>True while a server-initiated <see cref="Close"/> is in progress, so the receive loop treats the teardown as intentional and skips the unexpected-disconnect path.</summary>
        private volatile bool _isIntentionalClose;

        /// <summary>True when the peer is being closed because the client's heartbeat timed out, so the loss is treated as unexpected without double-reporting an error.</summary>
        private volatile bool _isHeartbeatTimeoutClose;

        /// <summary>0 until the receive loop is started, then 1. Set via <see cref="Interlocked"/> so <see cref="StartReceive"/> is idempotent and starts the loop exactly once whether called by the framework or the application.</summary>
        private int _receiving;

        /// <summary>Monotonic timestamp (ticks) of the last Ping received from the client, used to detect a silent/dead client.</summary>
        private long _lastPingReceivedTicks;

        /// <summary>Registration id of this peer's heartbeat-timeout tick on the shared scheduler.</summary>
        private long _heartbeatTickId;

        /// <summary>True once the heartbeat tick has been scheduled, so Close only unschedules if it was started.</summary>
        private bool _heartbeatScheduled;

        /// <summary>0 until the peer is closed, then 1. Set via <see cref="Interlocked"/> so <see cref="Close"/> — and thus <see cref="OnDisconnected"/> — runs exactly once no matter how many paths request it.</summary>
        private int _closed;

        /// <summary>True once this peer has begun terminal close/cleanup.</summary>
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;

        /// <summary>
        /// Initializes the peer from the accepted connection's <see cref="PeerInfo"/> and adopts its
        /// connection as the primary transport. The receive loop is not started until
        /// <see cref="StartReceive"/> is called.
        /// </summary>
        /// <param name="currentPeerInfo">The connection and metadata for the client this peer services.</param>
        protected BasePeer(PeerInfo currentPeerInfo) : base()
        {
            CurrentPeerInfo = currentPeerInfo;
            Connection = currentPeerInfo.Connection;
            InitDispatchGate(currentPeerInfo.Config.MaxInFlightMessages, currentPeerInfo.Config.SequentialDispatch);
        }

        /// <summary>
        /// Registers the peer's message handlers and starts the per-client receive loop (plus the heartbeat
        /// timeout watcher when enabled). Called by the server once the peer is ready to process traffic.
        /// </summary>
        /// <remarks>
        /// Idempotent: a second call is a no-op, so it is safe for both the framework (which calls it after
        /// <see cref="BaseServer.OnNewClient"/> returns) and application code to invoke it. The receive and
        /// heartbeat loops run fire-and-forget; the loop sets up the inbound Ping handler so the peer can answer with Pong.
        /// </remarks>
        public void StartReceive()
        {
            if (Interlocked.Exchange(ref _receiving, 1) != 0)
                return; // already started — framework and a manual call are both safe

            RegisterDataHandlers();

            if (CurrentPeerInfo.Config.HeartbeatEnabled)
            {
                Interlocked.Exchange(ref _lastPingReceivedTicks, MonotonicClock.Timestamp);
                RegisterDataHandler(SystemMessageTypes.Ping, OnPingReceived);
                _heartbeatScheduled = true;
                // Initial delay = timeout (grace period before the first liveness check), then every interval.
                _heartbeatTickId = TimerScheduler.Shared.Schedule(
                    CurrentPeerInfo.Config.HeartbeatIntervalMs, HeartbeatTick, CurrentPeerInfo.Config.HeartbeatTimeoutMs);
            }

            _ = HandlePeerAsync();
        }

        /// <summary>Both mode: attach the UDP connection once the handshake token has bound it to this peer.</summary>
        /// <param name="udp">The server-side UDP connection bound to this client by the handshake token.</param>
        /// <remarks>
        /// Stores the UDP channel on <see cref="CurrentPeerInfo"/> (so unreliable sends can be routed to it)
        /// and starts a secondary, fire-and-forget UDP receive loop. Invoked by the server's
        /// <c>PeerBinding</c>; not part of the public API.
        /// <para>
        /// Guards against a late bind: the TCP lifeline can tear the peer down (heartbeat timeout, IO error, kick)
        /// before the UDP handshake binds. If the peer is already closed we close the incoming connection instead
        /// of attaching it — otherwise it would be owned by a dead peer and never reaped (Both mode runs no UDP
        /// idle sweep). The post-store re-check closes the small window where <see cref="Close"/> ran after the
        /// guard but observed <see cref="PeerInfo.UdpConnection"/> still null.
        /// </para>
        /// </remarks>
        internal void AttachUdp(ITransportConnection udp)
        {
            if (Volatile.Read(ref _closed) != 0) { udp.Close(); return; }

            CurrentPeerInfo.UdpConnection = udp;

            if (Volatile.Read(ref _closed) != 0)
            {
                udp.Close(); // Close() raced in after our guard but before the store saw it; don't leak the channel.
                return;
            }

            _ = HandleUdpAsync(udp);
        }

        /// <summary>
        /// The primary inbound pump for this client: receives framed messages over the main (TCP) connection
        /// and dispatches them until the connection ends. Its finally block classifies how the client left
        /// (intentional close, error/heartbeat loss, or graceful close) and drives the corresponding callbacks.
        /// </summary>
        /// <returns>A task that completes when the receive loop exits and the peer's close flow has run.</returns>
        /// <remarks>
        /// Runs fire-and-forget. A null message indicates graceful EOF. Genuine errors (or a heartbeat
        /// timeout) trigger <see cref="OnUnexpectedDisconnect"/> before <see cref="Close"/>; an intentional
        /// close suppresses the error path. The TCP channel is the lifeline that governs the peer lifecycle.
        /// </remarks>
        private async Task HandlePeerAsync()
        {
            var hadError = false;

            try
            {
                while (Connection != null && Connection.IsConnected)
                {
                    var message = await Connection.ReceiveAsync().ConfigureAwait(false);
                    if (message == null) break;
                    var m = message.Value;

                    CurrentPeerInfo.Config.Metrics.IncrementMessagesReceived();
                    await DispatchAsync(m.Type, m.Payload).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                hadError = true;
                if (!_isIntentionalClose && !_isHeartbeatTimeoutClose)
                    SafeLifecycleHook(nameof(OnError), () => OnError($"Client {CurrentPeerInfo.Id} error: {ex.Message}"));
            }
            finally
            {
                var wasHeartbeat = _isHeartbeatTimeoutClose;
                _isHeartbeatTimeoutClose = false;

                if (_isIntentionalClose)
                    _isIntentionalClose = false;
                else if (hadError || wasHeartbeat)
                {
                    SafeLifecycleHook(nameof(OnUnexpectedDisconnect), OnUnexpectedDisconnect);
                    Close();
                }
                else
                    Close();
            }
        }

        /// <summary>
        /// Secondary inbound pump for the UDP channel in Both mode, dispatching unreliable messages received
        /// over UDP. Kept separate from the primary loop because UDP delivery is best-effort and must not
        /// affect the peer's lifecycle.
        /// </summary>
        /// <param name="udp">The UDP connection to read unreliable messages from.</param>
        /// <returns>A task that completes when the UDP channel closes or errors.</returns>
        /// <remarks>
        /// All exceptions are deliberately swallowed: UDP errors do not tear down the peer because the TCP
        /// channel is the lifeline that governs the disconnect flow.
        /// </remarks>
        // Secondary receive loop for the UDP channel in Both mode. UDP errors do not tear
        // down the peer — the TCP channel is the lifeline and drives the disconnect flow.
        private async Task HandleUdpAsync(ITransportConnection udp)
        {
            try
            {
                while (udp.IsConnected)
                {
                    var message = await udp.ReceiveAsync().ConfigureAwait(false);
                    if (message == null) break;
                    var m = message.Value;

                    CurrentPeerInfo.Config.Metrics.IncrementMessagesReceived();
                    await DispatchAsync(m.Type, m.Payload).ConfigureAwait(false);
                }
            }
            catch { /* ignored: TCP lifeline governs the peer lifecycle */ }
        }

        /// <summary>
        /// Heartbeat handler for inbound Ping frames: records the arrival time (so the timeout watcher sees
        /// the client as alive) and replies with a Pong so the client's own heartbeat loop stays satisfied.
        /// </summary>
        /// <param name="data">The Ping payload (empty by protocol; ignored).</param>
        private void OnPingReceived(byte[] data)
        {
            Interlocked.Exchange(ref _lastPingReceivedTicks, MonotonicClock.Timestamp);
            _ = Connection?.SendAsync(SystemMessageTypes.Pong, Array.Empty<byte>(), DeliveryMethod.Unreliable);
        }

        /// <summary>
        /// Heartbeat tick driven by the shared <see cref="TimerScheduler"/>: checks how long it has been since the
        /// last Ping from the client and closes the peer if the heartbeat timeout is exceeded, detecting clients that
        /// went silent without a clean disconnect (crash, network partition). Self-unschedules once the connection
        /// is gone.
        /// </summary>
        private void HeartbeatTick()
        {
            if (Connection == null || !Connection.IsConnected)
            {
                TimerScheduler.Shared.Unschedule(_heartbeatTickId);
                return;
            }

            var elapsed = MonotonicClock.ElapsedMs(Interlocked.Read(ref _lastPingReceivedTicks));
            if (elapsed > CurrentPeerInfo.Config.HeartbeatTimeoutMs)
            {
                _isHeartbeatTimeoutClose = true;
                SafeLifecycleHook(nameof(OnError), () => OnError($"Client {CurrentPeerInfo.Id} heartbeat timeout."));
                Connection?.Close();
                TimerScheduler.Shared.Unschedule(_heartbeatTickId);
            }
        }

        /// <summary>
        /// Serializes and sends a strongly-typed message to this client using the configured default
        /// delivery method. The primary convenience send path for application responses.
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        protected Task SendAsync<T>(ushort type, T message)
            => SendAsync(type, message, CurrentPeerInfo.Config.DefaultDelivery);

        /// <summary>
        /// Serializes and sends a strongly-typed message to this client using an explicit delivery method,
        /// routing the bytes to the appropriate channel (UDP for unreliable in Both mode, otherwise the
        /// primary connection).
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <param name="delivery">The delivery guarantee (e.g. Reliable or Unreliable) to use for this send.</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        protected Task SendAsync<T>(ushort type, T message, DeliveryMethod delivery)
            => SendAsync(type, message, delivery, 0);

        /// <summary>
        /// Serializes and sends a message to this client with explicit delivery and reliable-UDP channel. Distinct
        /// channels give independent reliable streams so a loss on one doesn't head-of-line block another.
        /// </summary>
        /// <typeparam name="T">The message type, serializable by MessagePack.</typeparam>
        /// <param name="type">The wire type id identifying the message.</param>
        /// <param name="message">The message instance to serialize and transmit.</param>
        /// <param name="delivery">The delivery guarantee for this send.</param>
        /// <param name="channel">Reliable-UDP channel id (ignored for TCP/unreliable).</param>
        /// <returns>A task that completes once the message has been handed to the transport.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the peer is closing or its connection is no longer connected.</exception>
        protected async Task SendAsync<T>(ushort type, T message, DeliveryMethod delivery, byte channel)
        {
            if (_isIntentionalClose || Connection == null || !Connection.IsConnected)
                throw new InvalidOperationException($"Cannot send: peer {CurrentPeerInfo.Id} is not connected.");

            var data = MessagePackSerializer.Serialize(message);
            CurrentPeerInfo.Config.Metrics.IncrementMessagesSent();
            await RouteSendAsync(type, data, delivery, channel).ConfigureAwait(false);
        }

        /// <summary>
        /// Selects the physical channel for an outgoing frame: in Both mode, unreliable traffic goes over
        /// the attached UDP channel while everything else (and any traffic before UDP attaches) uses the
        /// primary connection. Centralizes the reliable/unreliable routing decision.
        /// </summary>
        /// <param name="type">The wire type id of the message.</param>
        /// <param name="data">The already-serialized payload bytes.</param>
        /// <param name="delivery">The requested delivery guarantee, which determines channel selection.</param>
        /// <param name="channel">Reliable-UDP channel id passed through to the transport.</param>
        /// <returns>A task that completes once the chosen channel has accepted the frame.</returns>
        private Task RouteSendAsync(ushort type, byte[] data, DeliveryMethod delivery, byte channel)
        {
            // Both mode: route unreliable over the UDP channel once it is attached; everything
            // else (and any traffic before UDP attaches) goes over the primary connection.
            var udp = CurrentPeerInfo.UdpConnection;
            if (delivery == DeliveryMethod.Unreliable && udp != null && udp.IsConnected)
                return udp.SendAsync(type, data, delivery, channel);

            return Connection!.SendAsync(type, data, delivery, channel);
        }

        /// <summary>
        /// Flushes buffered sends to this peer when <see cref="Configuration.SendBatching"/> is enabled (a no-op
        /// otherwise). Call after composing a tick's messages to write them in one operation.
        /// </summary>
        /// <returns>A task that completes once buffered data has been written.</returns>
        public Task FlushAsync() => Connection?.FlushAsync() ?? Task.CompletedTask;

        /// <summary>
        /// Closes the peer (a server-initiated kick or the terminal step of any disconnect), marking the
        /// close as intentional, tearing down the underlying connection(s), removing the peer from the
        /// server pool, and firing <see cref="OnDisconnected"/>. Virtual so subclasses can run extra cleanup.
        /// </summary>
        /// <remarks>
        /// Setting <see cref="_isIntentionalClose"/> first is what makes the receive loop treat the ensuing
        /// connection end as intentional rather than an error. <see cref="PeerInfo.Disconnect"/> also
        /// detaches the peer from the owning server.
        /// </remarks>
        public virtual void Close()
        {
            // Exactly-once: whichever path closes first (server kick, receive-loop teardown, heartbeat timeout)
            // wins; later calls are no-ops, so OnDisconnected and the pool removal never run twice.
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _isIntentionalClose = true;
            if (_heartbeatScheduled) TimerScheduler.Shared.Unschedule(_heartbeatTickId);
            ShutdownDispatch();
            CurrentPeerInfo.Disconnect();
            SafeLifecycleHook(nameof(OnDisconnected), OnDisconnected);
        }

        /// <summary>Runs an application lifecycle hook without letting user code interrupt peer cleanup.</summary>
        private void SafeLifecycleHook(string hookName, Action hook)
        {
            try { hook(); }
            catch (Exception ex)
            {
                SafeLog($"{hookName} hook failed for peer {CurrentPeerInfo.Id}: {ex.Message}",
                    global::SetNet.Logging.LogLevel.Error);
            }
        }

        /// <summary>Logs best-effort; a throwing application logger must not escape lifecycle cleanup.</summary>
        private void SafeLog(string message, global::SetNet.Logging.LogLevel level)
        {
            try { CurrentPeerInfo.Config.Logger.Log(message, level); }
            catch { }
        }

        /// <summary>
        /// Binds every reflection-discovered server handler to its wire type id on the message processor, so
        /// inbound frames from this client are dispatched to the correct <see cref="SetNet.Data.IServerMessageHandler"/>.
        /// </summary>
        /// <remarks>Virtual so subclasses can extend or replace the default registration behavior.</remarks>
        protected virtual void RegisterDataHandlers()
        {
            foreach (var messageType in CurrentPeerInfo.CommandExecutor.Keys)
                RegisterDataHandler(messageType, CreateHandlerDelegate(messageType));
        }

        /// <summary>
        /// Logs a handler exception through the configured logger, including the peer id, overriding the base
        /// no-op so server-side handler failures are attributable and visible.
        /// </summary>
        /// <param name="type">The wire type id of the message whose handler threw.</param>
        /// <param name="exception">The exception raised by the failing handler.</param>
        protected override void HandleProcessingError(ushort type, Exception exception)
        {
            CurrentPeerInfo.Config.Logger.Log(
                $"Peer {CurrentPeerInfo.Id} message handler for type {type} failed: {exception.Message}",
                global::SetNet.Logging.LogLevel.Error);
        }

        /// <summary>Lifecycle hook invoked when this client's connection has closed (intentional kick, error, or graceful close).</summary>
        protected abstract void OnDisconnected();

        /// <summary>Hook invoked only on an unexpected error from this client (IO/socket error, crash, or heartbeat timeout) with a human-readable description.</summary>
        /// <param name="error">A message describing the error that occurred.</param>
        protected virtual void OnError(string error) { }

        /// <summary>Hook invoked when this client drops unexpectedly (crash or network failure), before the peer is closed; not fired on a graceful close or server-initiated kick.</summary>
        protected virtual void OnUnexpectedDisconnect() { }

        /// <summary>
        /// Builds the async dispatch delegate for a given message type that forwards this peer and the raw
        /// payload to the registered server handler. Factored out so the captured message type is bound
        /// correctly per handler.
        /// </summary>
        /// <param name="messageType">The wire type id whose handler the delegate should invoke.</param>
        /// <returns>A delegate that asynchronously routes payload bytes (with this peer) to the matching server handler.</returns>
        private Func<byte[], Task> CreateHandlerDelegate(ushort messageType)
        {
            return async data => await CurrentPeerInfo.CommandExecutor.Handlers[messageType].HandleAsync(this, data).ConfigureAwait(false);
        }
    }
}
