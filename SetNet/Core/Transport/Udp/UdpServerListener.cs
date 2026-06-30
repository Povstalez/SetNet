using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Server-side UDP acceptor. Owns ONE socket, reads every datagram, and demultiplexes by
    /// remote endpoint to per-peer <see cref="UdpServerConnection"/>s. Creates peers on handshake
    /// and expires silent ones. A test-only drop predicate can simulate packet loss.
    /// </summary>
    internal sealed class UdpServerListener : ITransportListener
    {
        /// <summary>Transport configuration: bind address/port, datagram limits, expiry timeout, and loss simulation.</summary>
        private readonly Configuration _config;

        /// <summary>Demux table mapping each remote endpoint to its per-peer connection; the routing key for inbound datagrams.</summary>
        private readonly ConcurrentDictionary<IPEndPoint, UdpServerConnection> _byEndpoint
            = new ConcurrentDictionary<IPEndPoint, UdpServerConnection>();

        /// <summary>Queue of freshly handshaked connections waiting to be handed out via <see cref="AcceptAsync"/> (pure-UDP mode only).</summary>
        private readonly AsyncQueue<AcceptedConnection> _accepted = new AsyncQueue<AcceptedConnection>();

        /// <summary>Serializes writes to the single shared socket, since <see cref="UdpClient"/> sends are not safe to overlap.</summary>
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // Both mode: tokens pre-registered by the TCP side. Bound connections are attached to an
        // existing peer instead of being surfaced through AcceptAsync.
        /// <summary>
        /// "Both" mode registry of expected handshake tokens, each mapped to a bind callback plus the time it was
        /// registered. Entries are removed when the matching UDP handshake binds them, when the TCP side
        /// unregisters them (setup failure), or by the pending-token sweep once they exceed their TTL — so a
        /// client that never completes the UDP handshake (firewall/symmetric-NAT TCP-only fallback) cannot leak
        /// an entry (and the peer object graph its callback captures) forever.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, PendingToken> _expectedTokens
            = new ConcurrentDictionary<Guid, PendingToken>();

        /// <summary>A pending Both-mode bind callback and the monotonic time it was registered, for TTL-based expiry.</summary>
        private sealed class PendingToken
        {
            /// <summary>Callback that attaches the bound UDP connection to its matching TCP peer.</summary>
            public Action<UdpServerConnection> OnBound = null!;

            /// <summary>Monotonic timestamp at which the token was registered, compared against the TTL by the sweep.</summary>
            public long RegisteredTicks;
        }

        /// <summary>When true, the listener runs in "Both" mode where the TCP side owns peer lifecycle (no AcceptAsync surfacing, no idle expiry).</summary>
        private readonly bool _boundMode;

        /// <summary>Test hook: return true to drop an inbound datagram (simulated loss).</summary>
        internal Func<byte[], bool>? DropInbound;

        /// <summary>RNG backing <see cref="Configuration.UdpSimulatedLossPercent"/> probabilistic inbound packet dropping.</summary>
        private readonly Random _rng = new Random();

        /// <summary>The single shared UDP socket bound at <see cref="Start"/>; null before start and after stop.</summary>
        private UdpClient? _socket;

        /// <summary>Cancellation source signalling the receive and expiry loops to stop when the listener shuts down.</summary>
        private CancellationTokenSource? _cts;

        /// <summary>Set while the listener is actively receiving; gates the background loops and guards double Stop.</summary>
        private volatile bool _running;

        /// <summary>Per-IP handshake rate limiter (no-op when disabled in config).</summary>
        private readonly RateLimiter _rateLimiter;

        /// <summary>
        /// Creates a UDP listener bound to the configured host/port on <see cref="Start"/>.
        /// </summary>
        /// <param name="config">Transport configuration controlling the bind endpoint, datagram limits, expiry, and loss simulation.</param>
        /// <param name="boundMode">
        /// When <c>true</c>, runs in "Both" mode: handshakes must match a pre-registered token and are bound to an
        /// existing TCP peer, and idle connections are never expired (the TCP side governs lifecycle).
        /// </param>
        public UdpServerListener(Configuration config, bool boundMode = false)
        {
            _config = config;
            _boundMode = boundMode;
            _rateLimiter = new RateLimiter(config.MaxConnectionsPerIpPerSecond);
        }

        /// <summary>
        /// Binds the shared UDP socket and launches the background receive loop (and, outside "Both" mode, the
        /// idle-expiry sweep). Must be called before connections can be accepted or datagrams processed.
        /// </summary>
        /// <remarks>In "Both" mode the expiry sweep is intentionally not started, because an alive-but-idle bound channel must not be reaped.</remarks>
        public void Start()
        {
            _socket = new UdpClient(new IPEndPoint(IPAddress.Parse(_config.Host), _config.EffectiveUdpPort));
            _cts = new CancellationTokenSource();
            _running = true;
            _ = ReceiveLoopAsync();
            // In Both mode the TCP peer governs lifecycle and closes the bound UDP connection,
            // so an idle (but alive) UDP channel must NOT be expired by the sweep. Instead, Both mode runs a
            // pending-token sweep so handshake tokens that never bind (TCP-only fallback) cannot leak.
            if (!_boundMode)
                _ = ExpirySweepAsync();
            else
                _ = PendingTokenSweepAsync();
        }

        /// <summary>
        /// Stops the listener: signals the background loops to cancel, closes the shared socket, and completes the
        /// accept queue so any pending <see cref="AcceptAsync"/> returns <c>null</c>.
        /// </summary>
        /// <remarks>Idempotent — a second call while already stopped is a no-op. Socket close exceptions are swallowed.</remarks>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            try { _socket?.Close(); } catch { }
            _accepted.Complete();
        }

        /// <summary>
        /// Awaits the next peer that has completed the UDP handshake (pure-UDP mode). In "Both" mode this never
        /// produces connections, since handshakes are bound to existing TCP peers rather than surfaced here.
        /// </summary>
        /// <param name="ct">Token to cancel the wait.</param>
        /// <returns>The next accepted connection, or <c>null</c> once the listener is stopped and the accept queue is drained.</returns>
        public async Task<AcceptedConnection?> AcceptAsync(CancellationToken ct = default)
        {
            var (ok, accepted) = await _accepted.DequeueAsync(ct).ConfigureAwait(false);
            return ok ? accepted : null;
        }

        /// <summary>Both mode: pre-register a token so the next handshake bearing it binds to a peer.</summary>
        /// <param name="token">The session token the TCP side negotiated and expects the client to present over UDP.</param>
        /// <param name="onBound">Callback invoked with the new connection once a handshake carrying <paramref name="token"/> arrives, used to attach it to the matching TCP peer.</param>
        /// <remarks>Prunes expired tokens first and refuses registration past <see cref="Configuration.MaxUdpPeers"/> pending entries, so a flood of Both-mode connects that never finish the UDP handshake cannot grow this table unbounded.</remarks>
        public void RegisterExpectedToken(Guid token, Action<UdpServerConnection> onBound)
        {
            PruneExpiredTokens();
            if (_config.MaxUdpPeers > 0 && _expectedTokens.Count >= _config.MaxUdpPeers)
                return; // at capacity: skip UDP binding for this client (it degrades to TCP-only)
            _expectedTokens[token] = new PendingToken { OnBound = onBound, RegisteredTicks = MonotonicClock.Timestamp };
        }

        /// <summary>Both mode: removes a previously registered token, e.g. when the TCP-side setup for that client failed.</summary>
        /// <param name="token">The token to drop so its bind callback (and the peer graph it captures) is released promptly.</param>
        public void UnregisterExpectedToken(Guid token) => _expectedTokens.TryRemove(token, out _);

        /// <summary>Removes pending Both-mode tokens older than the handshake TTL so unbound tokens (TCP-only fallback) do not accumulate.</summary>
        private void PruneExpiredTokens()
        {
            var ttl = _config.ConnectTimeoutMs > 0 ? _config.ConnectTimeoutMs : 10000;
            foreach (var kv in _expectedTokens)
                if (MonotonicClock.ElapsedMs(kv.Value.RegisteredTicks) > ttl)
                    _expectedTokens.TryRemove(kv.Key, out _);
        }

        /// <summary>
        /// Sends a pre-framed datagram to a specific remote through the shared socket. Serialized by a lock because
        /// a single <see cref="UdpClient"/> is shared across all peers and concurrent sends are unsafe.
        /// </summary>
        /// <param name="datagram">The buffer holding the framed datagram bytes.</param>
        /// <param name="count">The number of valid bytes in <paramref name="datagram"/> to transmit.</param>
        /// <param name="remote">The destination endpoint.</param>
        /// <returns>A task that completes once the send attempt finishes; send errors are swallowed (datagram dropped).</returns>
        public async Task SendRawAsync(byte[] datagram, int count, IPEndPoint remote)
        {
            var socket = _socket;
            if (socket == null) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try { await socket.SendAsync(datagram, count, remote).ConfigureAwait(false); }
            catch { /* drop on send error */ }
            finally { _sendLock.Release(); }
        }

        /// <summary>
        /// Sends the second leg of the two-way handshake, acknowledging the client's token so it knows the server
        /// has registered the session and can begin sending data.
        /// </summary>
        /// <param name="token">The session token to echo back in the acknowledgement.</param>
        /// <param name="remote">The endpoint that initiated the handshake.</param>
        private void SendHandshakeAck(Guid token, IPEndPoint remote)
        {
            var ack = UdpDatagram.BuildToken(PacketKind.HandshakeAck, token);
            _ = SendRawAsync(ack, ack.Length, remote);
        }

        /// <summary>
        /// Removes a connection from the demux table so its endpoint stops receiving datagrams. Called by the
        /// connection itself during <see cref="UdpServerConnection.Close"/>.
        /// </summary>
        /// <param name="conn">The connection to unregister, keyed by its <see cref="UdpServerConnection.Remote"/> endpoint.</param>
        public void RemoveConnection(UdpServerConnection conn)
            => _byEndpoint.TryRemove(conn.Remote, out _);

        /// <summary>
        /// The single receive loop draining the shared socket. Applies optional simulated/test packet loss, then
        /// dispatches each surviving datagram to its handler. This loop is the sole reader of the socket, which is
        /// what lets per-connection <see cref="UdpServerConnection.OnDatagram"/> be invoked without contention.
        /// </summary>
        /// <returns>A task that runs until the listener is stopped or the socket is closed.</returns>
        /// <remarks>Runs as a fire-and-forget background task started in <see cref="Start"/>; terminates silently when the socket closes.</remarks>
        private async Task ReceiveLoopAsync()
        {
            var socket = _socket!;
            try
            {
                while (_running)
                {
                    var result = await socket.ReceiveAsync().ConfigureAwait(false);
                    if (_config.UdpSimulatedLossPercent > 0 && _rng.Next(100) < _config.UdpSimulatedLossPercent)
                        continue; // simulated inbound loss
                    if (DropInbound != null && DropInbound(result.Buffer)) continue;
                    Dispatch(result.Buffer, result.RemoteEndPoint);
                }
            }
            catch { /* socket closed */ }
        }

        /// <summary>
        /// Top-level inbound router: handles handshake datagrams specially (new/duplicate sessions), ignores
        /// stray handshake-acks, and forwards all other datagrams to the matching per-endpoint connection.
        /// Datagrams from unknown endpoints are dropped.
        /// </summary>
        /// <param name="dg">The raw datagram bytes.</param>
        /// <param name="remote">The sender's endpoint, used as the demux key.</param>
        private void Dispatch(byte[] dg, IPEndPoint remote)
        {
            if (dg.Length < 1) return;

            if (dg[0] == PacketKind.Handshake)
            {
                HandleHandshake(dg, remote);
                return;
            }

            if (dg[0] == PacketKind.HandshakeAck) return; // 2-way handshake: server never consumes acks

            if (_byEndpoint.TryGetValue(remote, out var conn))
                conn.OnDatagram(dg);
            // else: unknown endpoint -> drop
        }

        /// <summary>
        /// Processes an inbound handshake datagram, establishing or refreshing a session. Re-acks idempotently for a
        /// retransmitted handshake on an existing token, replaces a stale session when the same endpoint presents a
        /// new token (e.g. reconnect), and otherwise creates a connection — binding it to a TCP peer in "Both" mode
        /// (only if its token was pre-registered) or surfacing it through <see cref="AcceptAsync"/> in pure-UDP mode.
        /// </summary>
        /// <param name="dg">The raw handshake datagram, expected to carry a session token.</param>
        /// <param name="remote">The endpoint that sent the handshake.</param>
        private void HandleHandshake(byte[] dg, IPEndPoint remote)
        {
            if (!UdpDatagram.TryParseToken(dg, out var token)) return;

            if (_byEndpoint.TryGetValue(remote, out var existing))
            {
                if (existing.Token == token)
                {
                    // Retransmitted handshake: re-ack idempotently.
                    SendHandshakeAck(existing.Token, remote);
                    return;
                }

                // Same endpoint, different token = a fresh session (e.g. reconnect). Drop the stale
                // connection and fall through to create a new one with fresh reliability state.
                existing.Close();
            }

            // Per-IP rate limit on new handshakes (retransmits hit the existing-endpoint path above).
            if (!_rateLimiter.Allow(remote.Address))
            {
                _config.Metrics.IncrementHandshakesDropped();
                return;
            }

            // Bound memory under a UDP handshake flood: refuse new peers past the cap (silent drop to
            // avoid log-flooding; the dropped count is tracked via metrics).
            if (_config.MaxUdpPeers > 0 && _byEndpoint.Count >= _config.MaxUdpPeers)
            {
                _config.Metrics.IncrementHandshakesDropped();
                return;
            }

            if (_boundMode)
            {
                if (!_expectedTokens.TryRemove(token, out var pending))
                    return; // unknown (or expired) token in Both mode -> ignore
                // Both mode: reliable traffic rides TCP, so the bound UDP leg needs no reliability channels.
                var bound = new UdpServerConnection(this, remote, token, _config, reliabilityEnabled: false);
                _byEndpoint[remote] = bound;
                pending.OnBound(bound);
                SendHandshakeAck(token, remote);
                return;
            }

            var conn = new UdpServerConnection(this, remote, token, _config);
            _byEndpoint[remote] = conn;
            _accepted.Enqueue(new AcceptedConnection(conn, token, remote));
            SendHandshakeAck(token, remote);
        }

        /// <summary>
        /// Background sweep that periodically reaps connections which have gone silent past
        /// <see cref="Configuration.UdpPeerExpiryMs"/>, since UDP has no connection teardown and a vanished client
        /// would otherwise linger forever. Not run in "Both" mode, where the TCP side owns lifecycle.
        /// </summary>
        /// <returns>A task that loops until the listener is stopped (cancellation) or the socket is torn down.</returns>
        /// <remarks>The sweep interval is a third of the expiry window (floored at 500ms) so expired peers are detected promptly without busy-spinning.</remarks>
        private async Task ExpirySweepAsync()
        {
            try
            {
                while (_running)
                {
                    await Task.Delay(Math.Max(500, _config.UdpPeerExpiryMs / 3), _cts!.Token).ConfigureAwait(false);
                    foreach (var kv in _byEndpoint)
                    {
                        if (kv.Value.IsExpired(_config.UdpPeerExpiryMs))
                            kv.Value.Close();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// Both-mode background sweep that periodically drops pending handshake tokens older than the handshake
        /// TTL. Without it, every client that opens TCP but never completes the UDP handshake (the documented
        /// firewall/symmetric-NAT TCP-only fallback) would leave its token — and the peer object graph the bind
        /// callback captures — pinned in <see cref="_expectedTokens"/> for the life of the process.
        /// </summary>
        /// <returns>A task that loops until the listener is stopped.</returns>
        private async Task PendingTokenSweepAsync()
        {
            var ttl = _config.ConnectTimeoutMs > 0 ? _config.ConnectTimeoutMs : 10000;
            try
            {
                while (_running)
                {
                    await Task.Delay(Math.Max(1000, ttl / 2), _cts!.Token).ConfigureAwait(false);
                    PruneExpiredTokens();
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>Disposes the listener by stopping it; lets the listener participate in <c>using</c> scopes and uniform cleanup.</summary>
        public void Dispose() => Stop();
    }
}
