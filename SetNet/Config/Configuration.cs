using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using SetNet.Core.Transport;
using SetNet.Diagnostics;
using SetNet.Logging;
using SetNet.Messaging;

namespace SetNet.Config
{
    /// <summary>
    /// Central settings object shared by clients, servers, and peers. It bundles every tunable knob — endpoint,
    /// buffering, reconnection, heartbeat, transport selection, and the UDP reliability layer — into one place so a
    /// single instance can configure both ends of a connection consistently.
    /// </summary>
    public class Configuration
    {
        /// <summary>Gets or sets the remote host name or IP address to connect to (client) or bind to (server).</summary>
        public string Host { get; set; }

        /// <summary>Gets or sets the TCP port used for the primary connection.</summary>
        public int Port { get; set; }

        /// <summary>Gets or sets the receive/send buffer size in bytes used for socket reads. Defaults to 4096.</summary>
        public int BufferSize { get; set; } = 4096;

        /// <summary>
        /// Disables Nagle's algorithm (sets <c>TcpClient.NoDelay</c>) on all TCP sockets. Defaults to <c>true</c>
        /// because this library's pattern is many small framed messages, where Nagle can add up to ~40ms latency;
        /// the library also offers its own opt-in <see cref="SendBatching"/>. Set to <c>false</c> to let the OS
        /// coalesce small writes (higher throughput, higher latency).
        /// </summary>
        public bool TcpNoDelay { get; set; } = true;

        /// <summary>Gets or sets the maximum number of simultaneous client connections the server will accept. Defaults to 100.</summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Gets or sets a value indicating whether the TCP stream is wrapped in TLS. Applies to the TCP channel
        /// (including Both mode's reliable channel); UDP datagrams are NOT encrypted. Defaults to <c>false</c>.
        /// </summary>
        public bool UseSsl { get; set; } = false;

        /// <summary>
        /// Server-side TLS certificate (with private key) presented to clients. Required when <see cref="UseSsl"/>
        /// is enabled on a server; ignored on the client.
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// Client-side: the host name expected in the server's certificate during the TLS handshake.
        /// Falls back to <see cref="Host"/> when null/empty.
        /// </summary>
        public string? SslTargetHost { get; set; }

        /// <summary>
        /// Client-side TLS certificate validation override (e.g. to accept a self-signed/pinned cert). When null,
        /// the platform's default chain validation is used.
        /// </summary>
        public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <summary>Gets or sets a value indicating whether the client automatically attempts to reconnect after an unexpected disconnect. Defaults to <c>false</c>.</summary>
        public bool AutoReconnect { get; set; } = false;

        /// <summary>Gets or sets the number of reconnection attempts made before giving up when <see cref="AutoReconnect"/> is enabled. Defaults to 3.</summary>
        public int MaxReconnectAttempts { get; set; } = 3;

        /// <summary>Gets or sets the delay, in milliseconds, between consecutive reconnection attempts. Defaults to 1000.</summary>
        public int ReconnectDelayMs { get; set; } = 1000;

        /// <summary>Gets or sets the maximum time, in milliseconds, to wait for a connection (and the initial handshake) to complete before failing. Defaults to 10000.</summary>
        public int ConnectTimeoutMs { get; set; } = 10000;

        /// <summary>Gets or sets a value indicating whether periodic Ping/Pong heartbeats are sent to detect dead connections. Defaults to <c>false</c>.</summary>
        public bool HeartbeatEnabled { get; set; } = false;

        /// <summary>Gets or sets the interval, in milliseconds, between outgoing heartbeat pings. Defaults to 5000.</summary>
        public int HeartbeatIntervalMs { get; set; } = 5000;

        /// <summary>Gets or sets the time, in milliseconds, without a heartbeat response after which the connection is considered dead. Defaults to 15000.</summary>
        public int HeartbeatTimeoutMs { get; set; } = 15000;

        /// <summary>Gets or sets the logging sink used throughout the library. Defaults to a <see cref="ConsoleLogger"/>; set to a custom <see cref="ILogger"/> to redirect diagnostics.</summary>
        public ILogger Logger { get; set; } = new ConsoleLogger();

        /// <summary>Backing field for <see cref="Serializer"/>; null means "use the process-wide <see cref="SetNetSerializer.Default"/>".</summary>
        private ISerializer? _serializer;

        /// <summary>
        /// Serializer used to encode/decode message payloads for this connection. When not set explicitly, it
        /// falls back to the process-wide <see cref="SetNetSerializer.Default"/> (MessagePack by default), so you
        /// can switch the whole app's format with one line at startup, or override it per connection here. Both
        /// ends of a connection must use the same serializer.
        /// </summary>
        public ISerializer Serializer
        {
            get => _serializer ?? SetNetSerializer.Default;
            set => _serializer = value;
        }

        /// <summary>
        /// Live traffic/health counters the library increments (messages, connections, retransmits, drops).
        /// Read or snapshot these to export to monitoring. Shared by the client/server using this configuration.
        /// </summary>
        public NetworkMetrics Metrics { get; } = new NetworkMetrics();

        // ── Transport selection ──────────────────────────────────────────────
        /// <summary>Which transport(s) to use. Default <see cref="TransportType.Tcp"/> = original behaviour.</summary>
        public TransportType TransportType { get; set; } = TransportType.Tcp;

        /// <summary>Delivery method used by the 2-argument <c>SendAsync(type, message)</c> overload.</summary>
        public DeliveryMethod DefaultDelivery { get; set; } = DeliveryMethod.Reliable;

        // ── UDP endpoint ─────────────────────────────────────────────────────
        /// <summary>UDP port. <c>0</c> means reuse <see cref="Port"/> (TCP and UDP can share a port number).</summary>
        public int UdpPort { get; set; } = 0;

        /// <summary>Effective UDP port (falls back to <see cref="Port"/> when <see cref="UdpPort"/> is 0).</summary>
        public int EffectiveUdpPort => UdpPort != 0 ? UdpPort : Port;

        // ── UDP emulated-connection timing ───────────────────────────────────
        /// <summary>Gets or sets the time, in milliseconds, to wait for the UDP connection handshake to complete before it is abandoned. Defaults to 5000.</summary>
        public int UdpHandshakeTimeoutMs { get; set; } = 5000;

        /// <summary>Gets or sets the interval, in milliseconds, at which an unacknowledged UDP handshake packet is retransmitted. Defaults to 250.</summary>
        public int UdpHandshakeRetransmitMs { get; set; } = 250;

        /// <summary>Gets or sets how long, in milliseconds, a UDP peer may be silent before its emulated connection is expired and removed. Defaults to 15000.</summary>
        public int UdpPeerExpiryMs { get; set; } = 15000;

        // ── UDP reliability layer ────────────────────────────────────────────
        /// <summary>
        /// Master toggle for the reliable UDP channel (sequence/ACK/retransmit/ordered).
        /// <para>
        /// <b>Security note:</b> UDP datagrams (data, ACK, and disconnect) carry no per-packet authentication or
        /// encryption. The reliability guarantees hold against benign packet loss, NOT against an adversary who
        /// can spoof the peer's source IP:port — such an attacker could inject or drop messages on the reliable
        /// stream. Route confidentiality/integrity-sensitive traffic over TLS-over-TCP (set <see cref="UseSsl"/>,
        /// or use <see cref="TransportType.Both"/> with reliable delivery, which rides the encrypted TCP channel).
        /// </para>
        /// </summary>
        public bool UdpReliabilityEnabled { get; set; } = true;

        /// <summary>
        /// Number of independent reliable-UDP channels (1..255). Each channel has its own sequence/ACK/ordering,
        /// so a lost packet on one channel does not head-of-line block another (e.g. chat vs movement). The
        /// per-message channel is chosen via the <c>channel</c> argument of <c>SendAsync</c>. Defaults to 1.
        /// </summary>
        public int UdpReliableChannels { get; set; } = 1;

        /// <summary>Gets or sets the time, in milliseconds, to wait for an ACK before a reliable UDP packet is retransmitted. Defaults to 100.</summary>
        public int UdpReliableAckTimeoutMs { get; set; } = 100;

        /// <summary>Gets or sets the maximum number of times a single reliable UDP packet is retransmitted before the connection is treated as failed. Defaults to 10.</summary>
        public int UdpReliableMaxRetransmits { get; set; } = 10;

        /// <summary>Gets or sets the size of the reliable UDP sliding window (number of in-flight unacknowledged packets). Must be 1..64 because the ACK is a 64-bit bitfield. Defaults to 64.</summary>
        public int UdpReliableWindowSize { get; set; } = 64;

        /// <summary>Gets or sets the maximum application payload, in bytes, carried in a single UDP datagram before fragmentation/rejection. Defaults to 1200 to stay under typical MTU.</summary>
        public int UdpMaxDatagramPayload { get; set; } = 1200;

        /// <summary>Gets or sets a value indicating whether reliable UDP delivery also preserves message ordering. Defaults to <c>true</c>.</summary>
        public bool UdpOrderedReliable { get; set; } = true;

        /// <summary>Test/diagnostics: drop this percent (0-100) of inbound UDP datagrams to simulate loss.</summary>
        public int UdpSimulatedLossPercent { get; set; } = 0;

        // ── Limits / DoS protection ──────────────────────────────────────────
        /// <summary>
        /// Maximum number of simultaneous server-side peers. The accept loop rejects (closes) further
        /// connections once this is reached. Defaults to <see cref="MaxConnections"/> when 0.
        /// </summary>
        public int MaxConnectionsLimit { get; set; } = 0;

        /// <summary>
        /// Maximum number of concurrent UDP peers the server will hold. Handshakes beyond this are dropped,
        /// bounding memory under a UDP handshake flood. Defaults to 1000.
        /// </summary>
        public int MaxUdpPeers { get; set; } = 1000;

        /// <summary>
        /// Largest allowed TCP frame payload, in bytes. A declared frame length above this closes the
        /// connection instead of letting the reassembly buffer grow unbounded (slow-loris protection).
        /// 0 disables the cap. Defaults to 1 MiB.
        /// </summary>
        public int MaxMessageSize { get; set; } = 1024 * 1024;

        /// <summary>
        /// Maximum new connections/handshakes accepted per remote IP per second. 0 disables per-IP rate
        /// limiting. Defaults to 0.
        /// </summary>
        public int MaxConnectionsPerIpPerSecond { get; set; } = 0;

        /// <summary>
        /// Maximum number of decoded-but-not-yet-dispatched inbound messages buffered per connection. Bounds
        /// memory when a peer sends faster than handlers consume: over the cap, best-effort (unreliable) messages
        /// are dropped (counted in <see cref="NetworkMetrics"/>) and a reliable stream that overflows tears the
        /// connection down rather than corrupting ordering or growing without bound (OOM). 0 disables the bound.
        /// Defaults to 16384.
        /// </summary>
        public int MaxInboundQueue { get; set; } = 16384;

        /// <summary>
        /// Maximum number of message handlers that may run concurrently per connection. When the limit is
        /// reached the receive loop pauses (back-pressure) before reading more, bounding memory if handlers
        /// are slow. 0 disables the limit (handlers run fire-and-forget, the original behaviour). Defaults to 0.
        /// </summary>
        public int MaxInFlightMessages { get; set; } = 0;

        /// <summary>
        /// When true, the receive loop awaits each message handler to completion before reading the next frame,
        /// guaranteeing in-order, non-overlapping handler execution on a single connection. By default
        /// (<c>false</c>) handlers are dispatched without waiting, so two messages can be processed concurrently
        /// or out of order even over an ordered TCP stream — enable this when handler ordering matters and you
        /// accept the reduced pipeline throughput. Takes precedence over <see cref="MaxInFlightMessages"/>.
        /// </summary>
        public bool SequentialDispatch { get; set; } = false;

        // ── Send batching ────────────────────────────────────────────────────
        /// <summary>
        /// When true, TCP sends are buffered and coalesced into one socket write instead of one write per message,
        /// cutting syscalls for the common game-tick pattern (many small messages per tick). Flushed automatically
        /// every <see cref="SendBatchFlushMs"/>, and on demand via the client/peer <c>FlushAsync()</c>.
        /// <b>Disabled by default</b> — opt in per server/client.
        /// </summary>
        public bool SendBatching { get; set; } = false;

        /// <summary>Auto-flush interval (ms) for the batched send buffer when <see cref="SendBatching"/> is on. Defaults to 15.</summary>
        public int SendBatchFlushMs { get; set; } = 15;

        /// <summary>
        /// Maximum time, in milliseconds, a single TCP socket write may take before it is abandoned and the
        /// connection is torn down. Bounds the case where a dead-but-not-reset / zero-window peer stops reading:
        /// without it a write parks in the kernel forever, holding the send lock and stalling every other send to
        /// that peer. The default (30000) only fires on a genuinely stuck peer — a healthy write to the OS send
        /// buffer completes in microseconds. Set to 0 to disable (restoring the original unbounded behaviour),
        /// or raise it for very large frames over very slow links.
        /// </summary>
        public int SendTimeoutMs { get; set; } = 30000;

        /// <summary>Effective peer cap: <see cref="MaxConnectionsLimit"/> if set, otherwise <see cref="MaxConnections"/>.</summary>
        public int EffectiveMaxConnections => MaxConnectionsLimit > 0 ? MaxConnectionsLimit : MaxConnections;

        /// <summary>
        /// Validates that the settings are internally consistent and within supported ranges, failing fast at
        /// connect/start time rather than allowing a misconfiguration to surface later as obscure runtime errors.
        /// UDP-specific bounds are only checked when a non-TCP transport is selected.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="Host"/> is empty; <see cref="Port"/> is outside 1..65535; <see cref="BufferSize"/>
        /// is non-positive; or, for UDP/Both transports, when <see cref="UdpReliableWindowSize"/> is outside 1..64,
        /// <see cref="UdpMaxDatagramPayload"/> is outside 16..65507, or <see cref="UdpSimulatedLossPercent"/> is
        /// outside 0..100.
        /// </exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException("Configuration.Host must be set.");
            if (Port < 1 || Port > 65535)
                throw new InvalidOperationException($"Configuration.Port ({Port}) must be in 1..65535.");
            if (BufferSize < 1)
                throw new InvalidOperationException($"Configuration.BufferSize ({BufferSize}) must be positive.");
            if (MaxMessageSize < 0)
                throw new InvalidOperationException($"Configuration.MaxMessageSize ({MaxMessageSize}) cannot be negative.");
            if (MaxUdpPeers < 0)
                throw new InvalidOperationException($"Configuration.MaxUdpPeers ({MaxUdpPeers}) cannot be negative.");
            if (MaxConnectionsPerIpPerSecond < 0)
                throw new InvalidOperationException($"Configuration.MaxConnectionsPerIpPerSecond ({MaxConnectionsPerIpPerSecond}) cannot be negative.");

            if (TransportType != TransportType.Tcp)
            {
                // Fail fast on the footgun where the default 2-arg SendAsync would use a reliable channel that
                // does not exist. In pure-UDP mode reliable delivery needs the reliability layer; in Both mode
                // reliable rides the TCP channel, so the toggle is irrelevant there.
                if (TransportType == TransportType.Udp && !UdpReliabilityEnabled && DefaultDelivery == DeliveryMethod.Reliable)
                    throw new InvalidOperationException(
                        "Configuration.DefaultDelivery is Reliable but UdpReliabilityEnabled is false on a UDP transport: " +
                        "every reliable send would throw. Enable UdpReliabilityEnabled or set DefaultDelivery = Unreliable.");

                // The reliability ACK is a 64-bit bitfield, so the in-flight window must fit within 64.
                if (UdpReliableWindowSize < 1 || UdpReliableWindowSize > 64)
                    throw new InvalidOperationException(
                        $"Configuration.UdpReliableWindowSize ({UdpReliableWindowSize}) must be in 1..64.");
                if (UdpReliableChannels < 1 || UdpReliableChannels > 255)
                    throw new InvalidOperationException(
                        $"Configuration.UdpReliableChannels ({UdpReliableChannels}) must be in 1..255.");
                if (UdpMaxDatagramPayload < 16 || UdpMaxDatagramPayload > 65507)
                    throw new InvalidOperationException(
                        $"Configuration.UdpMaxDatagramPayload ({UdpMaxDatagramPayload}) must be in 16..65507.");
                if (UdpSimulatedLossPercent < 0 || UdpSimulatedLossPercent > 100)
                    throw new InvalidOperationException(
                        $"Configuration.UdpSimulatedLossPercent ({UdpSimulatedLossPercent}) must be in 0..100.");
            }
        }
    }
}