using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;

namespace SetNet.Gateway
{
    /// <summary>
    /// A front-end **gateway / reverse proxy**: it accepts client connections and relays their frames, byte-for-byte, to a
    /// backend SetNet server chosen per client — and relays the backend's frames back. Because it forwards **raw** frames
    /// (via <c>OnRawFrame</c> + <c>SendRawAsync</c>) it never deserializes payloads, so it needs no serializer and stays
    /// cheap. Use it to shard players across backend nodes, terminate the public transport (e.g. WebSockets) in front of
    /// internal TCP backends, or add a routing layer without touching game code.
    /// </summary>
    public sealed class GatewayServer : BaseServer
    {
        private readonly Func<PeerInfo, Configuration> _backendSelector;

        /// <summary>
        /// Creates a gateway listening per <paramref name="listenConfig"/>. For each accepted client,
        /// <paramref name="backendSelector"/> returns the configuration of the backend to relay it to (route by anything
        /// on the <see cref="PeerInfo"/>, e.g. remote IP).
        /// </summary>
        public GatewayServer(Configuration listenConfig, Func<PeerInfo, Configuration> backendSelector)
            : base(listenConfig)
            => _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));

        /// <inheritdoc/>
        protected override BasePeer OnNewClient(PeerInfo peerInfo) => new GatewayPeer(peerInfo, _backendSelector(peerInfo));
    }

    /// <summary>The server-side half of one relayed client: owns a backend client and forwards the client's frames to it.</summary>
    internal sealed class GatewayPeer : BasePeer
    {
        private readonly GatewayBackendClient _backend;
        private readonly ConcurrentQueue<(ushort type, byte[] data)> _buffer = new ConcurrentQueue<(ushort, byte[])>();
        private volatile bool _ready;

        public GatewayPeer(PeerInfo info, Configuration backendConfig) : base(info)
        {
            _backend = new GatewayBackendClient(backendConfig, this);
            _ = ConnectBackendAsync();
        }

        private async Task ConnectBackendAsync()
        {
            try
            {
                await _backend.ConnectAsync().ConfigureAwait(false);
                _ready = true;
                while (_buffer.TryDequeue(out var frame))
                    await _backend.SendRawAsync(frame.type, frame.data).ConfigureAwait(false);
            }
            catch
            {
                CurrentPeerInfo.Disconnect();   // couldn't reach the backend — drop the client
            }
        }

        // Every application frame from the client is forwarded raw to the backend (buffered until the backend connects).
        protected override bool OnRawFrame(ushort type, byte[] data)
        {
            if (_ready) _ = _backend.SendRawAsync(type, data);
            else _buffer.Enqueue((type, data));
            return true;   // consumed — no typed dispatch on the gateway
        }

        internal Task ForwardToClientAsync(ushort type, byte[] data) => SendRawAsync(type, data);

        protected override void OnDisconnected() { try { _backend.Disconnect(); } catch { /* ignore */ } }
        protected override void OnError(string error) { }
    }

    /// <summary>The gateway's client to a backend: forwards the backend's frames back to the front-end client.</summary>
    internal sealed class GatewayBackendClient : BaseClient
    {
        private readonly GatewayPeer _peer;

        public GatewayBackendClient(Configuration config, GatewayPeer peer) : base(config) => _peer = peer;

        protected override bool OnRawFrame(ushort type, byte[] data)
        {
            _ = _peer.ForwardToClientAsync(type, data);
            return true;
        }

        protected override void OnConnected() { }
        protected override void OnDisconnected() { try { _peer.CurrentPeerInfo.Disconnect(); } catch { /* ignore */ } }
        protected override void OnError(string error) { }
    }
}
