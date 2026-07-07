using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Ping
{
    /// <summary>Ping channel operations. A <see cref="Op.Ping"/> carries the initiator's timestamp; the peer echoes it back as <see cref="Op.Pong"/>.</summary>
    internal enum Op : ushort { Ping = 1, Pong = 2 }

    internal static class PingCodec
    {
        public static byte[] Stamp() => BitConverter.GetBytes(Stopwatch.GetTimestamp());
        public static double ElapsedMs(byte[] body)
        {
            if (body == null || body.Length < 8) return -1;
            var sent = BitConverter.ToInt64(body, 0);
            var ticks = Stopwatch.GetTimestamp() - sent;
            return ticks * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>A peer's latency: the last sample and an exponentially-smoothed value (both in milliseconds).</summary>
    public sealed class PingStat
    {
        /// <summary>The most recent RTT sample (ms), or -1 if none yet.</summary>
        public double Last { get; private set; } = -1;
        /// <summary>Smoothed RTT (EWMA, ms), or -1 if none yet.</summary>
        public double Smoothed { get; private set; } = -1;

        internal void Record(double rtt, double alpha)
        {
            if (rtt < 0) return;
            Last = rtt;
            Smoothed = Smoothed < 0 ? rtt : Smoothed * (1 - alpha) + rtt * alpha;
        }
    }

    /// <summary>Options for the ping layer.</summary>
    public sealed class PingOptions
    {
        /// <summary>How often the server pings each peer (ms). Default 1000.</summary>
        public int IntervalMs { get; set; } = 1000;
        /// <summary>EWMA smoothing factor (0..1); higher reacts faster. Default 0.2.</summary>
        public double Smoothing { get; set; } = 0.2;
        /// <summary>When &gt; 0, the client also auto-measures its own RTT this often (ms). Default 0 (off — call <c>MeasureAsync</c>).</summary>
        public int ClientAutoPingMs { get; set; } = 0;
    }

    /// <summary>
    /// Server-side ping tracker: pings every connected peer on a timer and lets you read a player's round-trip latency at
    /// any moment. Requires the client to run <c>client.UsePing()</c> (which answers the pings).
    /// </summary>
    public sealed class PingServer : IDisposable
    {
        private static readonly ConcurrentDictionary<BaseServer, PingServer> Servers = new ConcurrentDictionary<BaseServer, PingServer>();

        private readonly PingOptions _options;
        private readonly ConcurrentDictionary<BasePeer, PingStat> _stats = new ConcurrentDictionary<BasePeer, PingStat>();
        private readonly Timer _timer;

        /// <summary>Raised when a peer's RTT is updated with a fresh sample.</summary>
        public event Action<BasePeer, double>? Updated;

        internal PingServer(PingOptions options)
        {
            _options = options;
            _timer = new Timer(_ => PingAll(), null, options.IntervalMs, options.IntervalMs);
        }

        internal static PingServer Enable(BaseServer server, PingOptions options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new PingServer(options);
                s.PeerConnected += peer => hub._stats.TryAdd(peer, new PingStat());
                s.PeerDisconnected += peer => hub._stats.TryRemove(peer, out _);
                return hub;
            });

        internal static PingServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        private void PingAll()
        {
            foreach (var peer in _stats.Keys)
            {
                try { _ = peer.PublishRawAsync(Channels.Ping, (ushort)Op.Ping, PingCodec.Stamp()); }
                catch { /* a dead peer is cleaned up on disconnect */ }
            }
        }

        internal void RecordPong(BasePeer peer, byte[] body)
        {
            if (!_stats.TryGetValue(peer, out var stat)) return;
            var rtt = PingCodec.ElapsedMs(body);
            stat.Record(rtt, _options.Smoothing);
            if (rtt >= 0) Updated?.Invoke(peer, stat.Smoothed);
        }

        /// <summary>The peer's smoothed RTT in milliseconds, or -1 if not measured yet.</summary>
        public double Of(BasePeer peer) => _stats.TryGetValue(peer, out var s) ? s.Smoothed : -1;

        /// <summary>The peer's most recent RTT sample in milliseconds, or -1.</summary>
        public double LastOf(BasePeer peer) => _stats.TryGetValue(peer, out var s) ? s.Last : -1;

        /// <summary>The full stat object for a peer, or null.</summary>
        public PingStat? StatOf(BasePeer peer) => _stats.TryGetValue(peer, out var s) ? s : null;

        /// <summary>Stops the ping timer.</summary>
        public void Dispose() => _timer.Dispose();
    }

    /// <summary>Auto-discovered channel service: echoes pings and records pongs.</summary>
    [ProtocolChannel(Channels.Ping)]
    public sealed class PingChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            switch ((Op)request.Op)
            {
                case Op.Ping:   // a client measuring its own ping → echo its stamp straight back
                    return request.Peer.PublishRawAsync(Channels.Ping, (ushort)Op.Pong, request.RawBody);
                case Op.Pong:   // the client's reply to our ping → record the RTT
                    PingServer.For(request.Peer.CurrentPeerInfo.Server)?.RecordPong(request.Peer, request.RawBody);
                    return Task.CompletedTask;
                default:
                    return Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Client-side ping helper: answers the server's pings (so the server can measure this player) and, on request, measures
    /// this client's own RTT to the server.
    /// </summary>
    public sealed class PingClient : IDisposable
    {
        private readonly BaseClient _client;
        private readonly IDisposable _onPing;
        private readonly IDisposable _onPong;
        private readonly Timer? _autoTimer;
        private readonly double _alpha;

        /// <summary>The last RTT sample this client measured to the server (ms), or -1.</summary>
        public double Last { get; private set; } = -1;
        /// <summary>Smoothed RTT (EWMA, ms), or -1.</summary>
        public double Smoothed { get; private set; } = -1;
        /// <summary>Raised when this client measures a fresh RTT.</summary>
        public event Action<double>? Updated;

        internal PingClient(BaseClient client, PingOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _alpha = options.Smoothing;

            // Answer the server's pings so it can measure us.
            _onPing = client.OnRaw(Channels.Ping, (ushort)Op.Ping,
                body => { _ = client.PostRawAsync(Channels.Ping, (ushort)Op.Pong, body); });

            // Our own ping: the server echoes our stamp back → compute RTT.
            _onPong = client.OnRaw(Channels.Ping, (ushort)Op.Pong, body =>
            {
                var rtt = PingCodec.ElapsedMs(body);
                if (rtt < 0) return;
                Last = rtt;
                Smoothed = Smoothed < 0 ? rtt : Smoothed * (1 - _alpha) + rtt * _alpha;
                Updated?.Invoke(Smoothed);
            });

            if (options.ClientAutoPingMs > 0)
                _autoTimer = new Timer(_ => { _ = MeasureAsync(); }, null, options.ClientAutoPingMs, options.ClientAutoPingMs);
        }

        /// <summary>Sends one ping to the server; the result lands in <see cref="Last"/>/<see cref="Smoothed"/> when the pong returns.</summary>
        public Task MeasureAsync() => _client.PostRawAsync(Channels.Ping, (ushort)Op.Ping, PingCodec.Stamp());

        /// <summary>Stops auto-pinging and unsubscribes.</summary>
        public void Dispose()
        {
            _autoTimer?.Dispose();
            _onPing.Dispose();
            _onPong.Dispose();
        }
    }

    /// <summary>Enables the ping layer on a server / client.</summary>
    public static class PingExtensions
    {
        /// <summary>Starts server-side ping tracking; returns the tracker (read <c>Of(peer)</c> any time).</summary>
        public static PingServer UsePing(this BaseServer server, PingOptions? options = null)
            => PingServer.Enable(server ?? throw new ArgumentNullException(nameof(server)), options ?? new PingOptions());

        /// <summary>Installs the client-side ping responder + measurer; returns it.</summary>
        public static PingClient UsePing(this BaseClient client, PingOptions? options = null)
            => new PingClient(client ?? throw new ArgumentNullException(nameof(client)), options ?? new PingOptions());
    }

    /// <summary>One-time bootstrap so the ping channel service is discovered. Call at startup.</summary>
    public static class PingRuntime
    {
        /// <summary>Ensures the ping layer is discoverable.</summary>
        public static void Enable() { _ = typeof(PingChannelService); }
    }
}
