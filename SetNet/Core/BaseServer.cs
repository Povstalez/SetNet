using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Commands;
using SetNet.Core.Transport;
using SetNet.Core.Transport.Tcp;
using SetNet.Core.Transport.Udp;
using SetNet.Data;

namespace SetNet.Core
{
    /// <summary>
    /// Abstract server endpoint that listens for incoming connections, creates a <see cref="BasePeer"/> per
    /// accepted client, and tracks the live peer pool. It is the server-side counterpart to
    /// <see cref="BaseClient"/>: concrete servers subclass it and implement <see cref="OnNewClient"/> to
    /// produce their peer type. Supports TCP, UDP, or a combined "Both" mode where each client gets a
    /// reliable TCP channel plus a token-bound UDP channel for unreliable traffic.
    /// </summary>
    public abstract class BaseServer : IDisposable
    {
        /// <summary>The pool of currently connected peers, keyed by peer id. Guarded by locking on the dictionary itself.</summary>
        private readonly Dictionary<Guid, BasePeer> _clients = new Dictionary<Guid, BasePeer>();

        /// <summary>Listener and behavior settings (host, ports, transport type, heartbeat, logger).</summary>
        private readonly Configuration _config;

        /// <summary>Reflection-discovered registry of server-side message handlers, shared with every peer for dispatch.</summary>
        private readonly CommandExecutor<IServerMessageHandler> _commandExecutor;

        /// <summary>The primary (TCP or UDP) listener accepting incoming connections; null until started.</summary>
        private ITransportListener? _listener;

        /// <summary>The auxiliary UDP listener used only in Both mode for the unreliable channel; null otherwise.</summary>
        private UdpServerListener? _udpListener;

        /// <summary>Signals the accept loop and registered listeners to stop; also acts as the "already started" flag.</summary>
        private CancellationTokenSource _cts;

        /// <summary>Guards against double-dispose.</summary>
        private bool _disposed;

        /// <summary>Per-IP connection rate limiter for the TCP accept path (no-op when disabled in config).</summary>
        private readonly RateLimiter _rateLimiter;

        /// <summary>
        /// Initializes the server with its configuration and builds the server-side handler registry via
        /// reflection. No sockets are opened until <see cref="StartAsync"/> is called.
        /// </summary>
        /// <param name="config">Listener and behavior settings governing this server.</param>
        protected BaseServer(Configuration config)
        {
            _config = config;
            _commandExecutor = new CommandExecutor<IServerMessageHandler>();
            _rateLimiter = new RateLimiter(config.MaxConnectionsPerIpPerSecond);
        }

        /// <summary>
        /// Starts the server and runs the accept loop, creating and registering a peer for every incoming
        /// connection until cancelled. This is the long-running entry point that keeps the server online;
        /// in Both mode it delegates to <see cref="StartBothAsync"/>.
        /// </summary>
        /// <returns>A task that completes when the accept loop ends (i.e. the server is stopped or the listener closes).</returns>
        /// <exception cref="InvalidOperationException">Thrown if the server has already been started or is starting.</exception>
        /// <remarks>
        /// The non-null <see cref="_cts"/> doubles as the "already started" guard. The cancellation token is
        /// registered to stop the listener so <see cref="StopAsync"/>/<see cref="Dispose()"/> unblock the
        /// accept call. Each accepted connection produces a <see cref="PeerInfo"/>, is handed to
        /// <see cref="OnNewClient"/>, and is added to the peer pool under lock.
        /// </remarks>
        public async Task StartAsync()
        {
            if (_cts != null)
                throw new InvalidOperationException("Server is already started or starting.");
            _config.Validate();

            _cts = new CancellationTokenSource();

            if (_config.TransportType == TransportType.Both)
            {
                await StartBothAsync().ConfigureAwait(false);
                return;
            }

            _listener = TransportFactory.CreateListener(_config);
            _listener.Start();
            _cts.Token.Register(() => _listener.Stop());

            _config.Logger.Log($"Server started on {_config.Host}:{_config.Port}", global::SetNet.Logging.LogLevel.Info);

            while (!_cts.IsCancellationRequested)
            {
                AcceptedConnection? accepted;
                try
                {
                    accepted = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // AcceptAsync already absorbs per-connection faults; an escape here means the listener itself
                    // is unusable, so log and exit the loop rather than spinning on a permanent fault.
                    _config.Logger.Log($"Accept loop terminated: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    break;
                }
                if (accepted == null) break;

                if (accepted.RemoteEndPoint != null && !_rateLimiter.Allow(accepted.RemoteEndPoint.Address))
                {
                    _config.Metrics.IncrementConnectionsRejected();
                    accepted.Connection.Close();
                    continue;
                }

                if (IsAtCapacity())
                {
                    _config.Logger.Log($"Connection rejected: at capacity ({_config.EffectiveMaxConnections}).", global::SetNet.Logging.LogLevel.Warning);
                    _config.Metrics.IncrementConnectionsRejected();
                    accepted.Connection.Close();
                    continue;
                }

                var peerInfo = new PeerInfo(accepted.Connection, _config, this, _commandExecutor);

                BasePeer peer;
                try
                {
                    peer = OnNewClient(peerInfo);
                }
                catch (Exception ex)
                {
                    // A failing OnNewClient must not kill the accept loop.
                    _config.Logger.Log($"OnNewClient failed for {peerInfo.Id}: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    accepted.Connection.Close();
                    continue;
                }

                if (!TryRegisterPeer(peerInfo, peer))
                {
                    accepted.Connection.Close();
                    continue;
                }

                try
                {
                    peer.StartReceive(); // idempotent: ensures the receive loop runs even if OnNewClient didn't start it
                }
                catch (Exception ex)
                {
                    // A failing StartReceive must not kill the accept loop or leave a registered dead peer.
                    _config.Logger.Log($"StartReceive failed for {peerInfo.Id}: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    peer.Close();
                    accepted.Connection.Close();
                    RemoveClient(peerInfo);
                    continue;
                }

                _config.Metrics.IncrementConnectionsAccepted();
                _config.Logger.Log($"Client connected: {peerInfo.Id}", global::SetNet.Logging.LogLevel.Info);
            }
        }

        /// <summary>Number of currently connected peers (thread-safe snapshot).</summary>
        public int ActiveConnections
        {
            get { lock (_clients) return _clients.Count; }
        }

        /// <summary>True when the peer pool has reached <see cref="Configuration.EffectiveMaxConnections"/>.</summary>
        private bool IsAtCapacity()
        {
            lock (_clients) return _clients.Count >= _config.EffectiveMaxConnections;
        }

        /// <summary>
        /// Runs the accept loop for combined TCP+UDP ("Both") mode, where each client receives a reliable
        /// TCP channel and a separate UDP channel for unreliable traffic. Exists so a single logical peer
        /// can span two physical transports, bound together by a one-time handshake token.
        /// </summary>
        /// <returns>A task that completes when the TCP accept loop ends.</returns>
        /// <remarks>
        /// For each accepted TCP connection, a unique bind token is sent as the very first TCP frame
        /// (before <see cref="OnNewClient"/>, which may send application data) so the client never has to
        /// discard app frames while awaiting the token. A <see cref="PeerBinding"/> bridges the gap until
        /// the peer exists, attaching the UDP connection exactly once regardless of arrival order.
        /// </remarks>
        // Both mode: accept TCP peers, then hand each one a UDP bind token over TCP and bind
        // the subsequent UDP handshake (matched by token) to that same peer.
        private async Task StartBothAsync()
        {
            var tcp = new TcpListenerAdapter(_config);
            var udp = new UdpServerListener(_config, boundMode: true);
            _listener = tcp;
            _udpListener = udp;
            tcp.Start();
            udp.Start();
            _cts.Token.Register(() => { tcp.Stop(); udp.Stop(); });

            _config.Logger.Log(
                $"Server started (Both) on tcp {_config.Host}:{_config.Port} / udp {_config.EffectiveUdpPort}",
                global::SetNet.Logging.LogLevel.Info);

            while (!_cts.IsCancellationRequested)
            {
                AcceptedConnection? accepted;
                try
                {
                    accepted = await tcp.AcceptAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _config.Logger.Log($"Accept loop terminated: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    break;
                }
                if (accepted == null) break;

                if (accepted.RemoteEndPoint != null && !_rateLimiter.Allow(accepted.RemoteEndPoint.Address))
                {
                    _config.Metrics.IncrementConnectionsRejected();
                    accepted.Connection.Close();
                    continue;
                }

                if (IsAtCapacity())
                {
                    _config.Logger.Log($"Connection rejected: at capacity ({_config.EffectiveMaxConnections}).", global::SetNet.Logging.LogLevel.Warning);
                    _config.Metrics.IncrementConnectionsRejected();
                    accepted.Connection.Close();
                    continue;
                }

                var peerInfo = new PeerInfo(accepted.Connection, _config, this, _commandExecutor);

                // Send the bind token as the FIRST TCP frame (before OnNewClient, which may send app
                // data) so the client never has to discard application frames while awaiting the token.
                // A PeerBinding bridges the gap until OnNewClient produces the peer to attach to.
                var token = Guid.NewGuid();
                var binding = new PeerBinding();
                udp.RegisterExpectedToken(token, binding.Attach);

                BasePeer peer;
                try
                {
                    await accepted.Connection.SendAsync(SystemMessageTypes.UdpBindToken, token.ToByteArray(), DeliveryMethod.Reliable).ConfigureAwait(false);
                    peer = OnNewClient(peerInfo);
                }
                catch (Exception ex)
                {
                    // Setup failed after the token was registered: drop the token so its bind callback is released,
                    // AND abandon the binding so that if the UDP handshake won the race and already bound a
                    // UdpServerConnection, that connection is closed (and removed from the listener's demux table)
                    // rather than leaking with no peer to own it — Both mode runs no UDP idle sweep to reap it.
                    udp.UnregisterExpectedToken(token);
                    binding.Abandon();
                    _config.Logger.Log($"Both-mode setup failed for {peerInfo.Id}: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    accepted.Connection.Close();
                    continue;
                }

                if (!TryRegisterPeer(peerInfo, peer))
                {
                    udp.UnregisterExpectedToken(token);
                    binding.Abandon();
                    accepted.Connection.Close();
                    continue;
                }

                binding.SetPeer(peer);

                try
                {
                    peer.StartReceive(); // idempotent: ensures the receive loop runs even if OnNewClient didn't start it
                }
                catch (Exception ex)
                {
                    udp.UnregisterExpectedToken(token);
                    binding.Abandon();
                    _config.Logger.Log($"StartReceive failed for {peerInfo.Id}: {ex.Message}", global::SetNet.Logging.LogLevel.Error);
                    peer.Close();
                    accepted.Connection.Close();
                    RemoveClient(peerInfo);
                    continue;
                }

                _config.Metrics.IncrementConnectionsAccepted();
                _config.Logger.Log($"Client connected: {peerInfo.Id}", global::SetNet.Logging.LogLevel.Info);
            }
        }

        /// <summary>
        /// Registers a peer in the live pool only if it has not already disconnected during application setup.
        /// This closes the race where user code starts receive inside OnNewClient and the peer closes before the
        /// accept loop reaches the dictionary add.
        /// </summary>
        private bool TryRegisterPeer(PeerInfo peerInfo, BasePeer peer)
        {
            lock (_clients)
            {
                if (peer.IsClosed || peerInfo.IsDisconnected || !peerInfo.Connection.IsConnected)
                    return false;

                _clients[peerInfo.Id] = peer;
                return true;
            }
        }

        /// <summary>
        /// Small synchronization helper used only by Both mode to rendezvous the UDP token-bind callback
        /// with the peer produced by <see cref="OnNewClient"/>. Because the UDP handshake may complete
        /// before or after the peer is created, this class buffers whichever arrives first and attaches the
        /// UDP connection to the peer exactly once.
        /// </summary>
        // Bridges the UDP token-bind callback (which may fire before or after OnNewClient returns)
        // to the peer, so the UDP connection attaches exactly once regardless of ordering.
        private sealed class PeerBinding
        {
            /// <summary>Guards the peer/pending fields against the concurrent Attach and SetPeer callers.</summary>
            private readonly object _lock = new object();

            /// <summary>The bound peer once <see cref="SetPeer"/> has run; null until then.</summary>
            private BasePeer? _peer;

            /// <summary>A UDP connection that arrived before the peer existed, held until <see cref="SetPeer"/> can attach it.</summary>
            private UdpServerConnection? _pending;

            /// <summary>Set when setup failed before a peer was produced; a late <see cref="Attach"/> then closes its connection instead of stashing it.</summary>
            private bool _abandoned;

            /// <summary>
            /// Called by the UDP listener when the handshake token matches: attaches the UDP connection to
            /// the peer if it already exists, otherwise stashes it as pending for <see cref="SetPeer"/>.
            /// If the binding was abandoned (setup failed with no peer), the connection is closed immediately.
            /// </summary>
            /// <param name="conn">The newly bound server-side UDP connection for this client.</param>
            public void Attach(UdpServerConnection conn)
            {
                lock (_lock)
                {
                    if (_abandoned) { conn.Close(); return; }
                    if (_peer != null) _peer.AttachUdp(conn);
                    else _pending = conn;
                }
            }

            /// <summary>
            /// Abandons the binding because peer setup failed before a peer was produced. Closes any UDP
            /// connection the handshake already stashed, and makes any later <see cref="Attach"/> close its
            /// connection too — so a bound UDP connection is never left orphaned in the listener's demux table.
            /// </summary>
            public void Abandon()
            {
                lock (_lock)
                {
                    _abandoned = true;
                    _pending?.Close();
                    _pending = null;
                }
            }

            /// <summary>
            /// Supplies the peer once <see cref="OnNewClient"/> has produced it, attaching any UDP
            /// connection that arrived earlier so neither ordering of the two events drops the channel.
            /// </summary>
            /// <param name="peer">The peer that should own the UDP channel for this client.</param>
            public void SetPeer(BasePeer peer)
            {
                lock (_lock)
                {
                    _peer = peer;
                    if (_pending != null)
                    {
                        peer.AttachUdp(_pending);
                        _pending = null;
                    }
                }
            }
        }

        /// <summary>
        /// Stops the server: cancels the accept loop, shuts down the listeners, and closes every connected
        /// peer, clearing the pool. The graceful counterpart to <see cref="StartAsync"/>.
        /// </summary>
        /// <returns>A completed task (the operation is synchronous but returns a task for await-friendly call sites).</returns>
        /// <remarks>Safe to call when not started; the null-conditional operators make it a no-op in that case.</remarks>
        public Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _udpListener?.Stop();
            // Snapshot then clear under the lock, and Close() the peers OUTSIDE the lock: each Close() re-enters
            // RemoveClient (which locks _clients and removes the peer), so closing over a copy of an
            // already-cleared dictionary avoids mutating the collection while it is being enumerated.
            BasePeer[] snapshot;
            lock (_clients)
            {
                snapshot = new BasePeer[_clients.Count];
                _clients.Values.CopyTo(snapshot, 0);
                _clients.Clear();
            }
            foreach (var client in snapshot)
                client.Close();

            _config.Logger.Log("Server stopped", global::SetNet.Logging.LogLevel.Info);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Removes a disconnected client from the live peer pool. Called by a peer when it closes so the
        /// server no longer tracks or broadcasts to a dead connection.
        /// </summary>
        /// <param name="peerInfo">The metadata of the peer to remove, identified by its <see cref="PeerInfo.Id"/>.</param>
        /// <remarks>Thread-safe via locking on the peer dictionary.</remarks>
        public void RemoveClient(PeerInfo peerInfo)
        {
            lock (_clients)
            {
                _clients.Remove(peerInfo.Id);
            }
        }

        /// <summary>
        /// Factory hook implemented by concrete servers to create the peer instance for a newly accepted
        /// client. This is the primary extension point that binds the framework's connection handling to an
        /// application-specific peer type.
        /// </summary>
        /// <param name="peerInfo">The connection and metadata for the accepted client.</param>
        /// <returns>A new <see cref="BasePeer"/> (subclass) that will service this client.</returns>
        protected abstract BasePeer OnNewClient(PeerInfo peerInfo);

        /// <summary>
        /// Releases all resources held by the server, stopping listeners and closing all peers. Implements
        /// <see cref="IDisposable"/> and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Core dispose logic following the standard dispose pattern: cancels the accept loop, stops the
        /// listeners, and closes/clears all peers. Subclasses may override to release additional resources.
        /// </summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/> (managed resources should be released); false from a finalizer.</param>
        /// <remarks>Idempotent via the <see cref="_disposed"/> guard.</remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;

            _cts?.Cancel();
            _listener?.Stop();
            _udpListener?.Stop();
            BasePeer[] snapshot;
            lock (_clients)
            {
                snapshot = new BasePeer[_clients.Count];
                _clients.Values.CopyTo(snapshot, 0);
                _clients.Clear();
            }
            foreach (var client in snapshot) // close outside the lock over a copy (see StopAsync)
                client.Close();
            _cts?.Dispose();
        }
    }
}
