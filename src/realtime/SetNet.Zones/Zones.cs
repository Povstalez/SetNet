using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Zones
{
    /// <summary>Command operations (client → server) within the Zones protocol channel.</summary>
    internal enum ZoneOp : ushort
    {
        /// <summary>Claim a handoff token on the destination node.</summary>
        Claim = 1,
    }

    /// <summary>Push events (server → client) within the Zones protocol channel.</summary>
    internal enum ZoneEvt : ushort
    {
        /// <summary>Instructs the client to migrate to another node/zone.</summary>
        Transfer = 10,
    }

    /// <summary>Thrown when a zone claim fails (unknown/expired token, timeout).</summary>
    public sealed class ZoneException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public ZoneException(string message) : base(message) { }
    }

    /// <summary>The destination of a zone transfer — which node to connect to for the target zone.</summary>
    public sealed class ZoneTarget
    {
        /// <summary>Application-defined zone/region identifier.</summary>
        public string ZoneId { get; set; } = "";

        /// <summary>Host of the node that owns the target zone.</summary>
        public string Host { get; set; } = "";

        /// <summary>Port of the node that owns the target zone.</summary>
        public int Port { get; set; }

        /// <summary>Creates an empty target (for serialization).</summary>
        public ZoneTarget() { }

        /// <summary>Creates a target for <paramref name="zoneId"/> at <paramref name="host"/>:<paramref name="port"/>.</summary>
        public ZoneTarget(string zoneId, string host, int port) { ZoneId = zoneId; Host = host; Port = port; }
    }

    /// <summary>The migration instruction pushed to a client: where to reconnect and the one-time claim token.</summary>
    public sealed class ZoneTransfer
    {
        /// <summary>The destination node + zone.</summary>
        public ZoneTarget Target { get; internal set; } = new ZoneTarget();

        /// <summary>One-time token to present to the destination node's <c>ClaimAsync</c> to retrieve carried state.</summary>
        public string Token { get; internal set; } = "";
    }

    // ---- handoff store ----

    /// <summary>A carried handoff: the player's key, target zone, and opaque state, held until claimed or expired.</summary>
    public sealed class ZoneHandoff
    {
        /// <summary>The migrating player's stable key.</summary>
        public string PlayerKey { get; set; } = "";

        /// <summary>The destination zone id.</summary>
        public string ZoneId { get; set; } = "";

        /// <summary>Opaque carried state (serialized player snapshot — the zones layer never inspects it).</summary>
        public byte[] State { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Where handoff payloads live between the origin node stashing them and the destination node claiming them.
    /// The default <see cref="MemoryHandoffStore"/> is per-process (fine for co-located nodes / tests); for a real
    /// multi-node cluster supply a <b>shared</b> store (Redis/DB) so the destination node — a different process — can
    /// read what the origin wrote.
    /// </summary>
    public interface IHandoffStore
    {
        /// <summary>Stashes a handoff under a one-time <paramref name="token"/> with a time-to-live.</summary>
        Task PutAsync(string token, ZoneHandoff handoff, TimeSpan ttl);

        /// <summary>Atomically retrieves and removes a handoff by token; null when unknown or expired.</summary>
        Task<ZoneHandoff?> TakeAsync(string token);
    }

    /// <summary>In-process handoff store with TTL sweeping. Use a shared store for cross-process/cluster handoff.</summary>
    public sealed class MemoryHandoffStore : IHandoffStore
    {
        private sealed class Entry { public ZoneHandoff Handoff = null!; public long ExpiresTicks; }

        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>();

        /// <inheritdoc/>
        public Task PutAsync(string token, ZoneHandoff handoff, TimeSpan ttl)
        {
            Sweep();
            _entries[token] = new Entry
            {
                Handoff = handoff,
                ExpiresTicks = Stopwatch.GetTimestamp() + (long)(ttl.TotalSeconds * Stopwatch.Frequency),
            };
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<ZoneHandoff?> TakeAsync(string token)
        {
            if (_entries.TryRemove(token ?? "", out var entry) && entry.ExpiresTicks >= Stopwatch.GetTimestamp())
                return Task.FromResult<ZoneHandoff?>(entry.Handoff);
            return Task.FromResult<ZoneHandoff?>(null);
        }

        private void Sweep()
        {
            var now = Stopwatch.GetTimestamp();
            List<string>? drop = null;
            foreach (var kv in _entries) if (kv.Value.ExpiresTicks < now) (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _entries.TryRemove(k, out _);
        }
    }

    /// <summary>Settings for the zone handoff service.</summary>
    public sealed class ZoneOptions
    {
        /// <summary>Maps a connected peer to its stable player key (default = connection id; override for cross-node identity).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>How long a stashed handoff is valid before it's swept (default 60 s — enough to reconnect).</summary>
        public TimeSpan HandoffTtl { get; set; } = TimeSpan.FromSeconds(60);
    }

    // ---- wire ----

    /// <summary>
    /// Body codecs for the Zones channel. The unified protocol envelope already carries kind/channel/op/correlation,
    /// so these encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic.
    /// </summary>
    internal static class ZoneWire
    {
        /// <summary>Claim-command body: the handoff token.</summary>
        public static byte[] EncodeClaim(string token)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) w.Write(token ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a claim-command body.</summary>
        public static string DecodeClaim(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }

        /// <summary>Claim reply body: the claimed zone id and the carried state.</summary>
        public static byte[] EncodeClaimReply(string zoneId, byte[] state)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(zoneId ?? "");
            w.Write(state?.Length ?? 0);
            if (state != null) w.Write(state);
            return ms.ToArray();
        }

        /// <summary>Reads a claim reply body.</summary>
        public static (string zoneId, byte[] state) DecodeClaimReply(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var zoneId = r.ReadString();
            var len = r.ReadInt32();
            var state = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return (zoneId, state);
        }

        /// <summary>Transfer event body: [zone id][host][port][token].</summary>
        public static byte[] EncodeTransfer(string zoneId, string host, int port, string token)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(zoneId ?? "");
            w.Write(host ?? "");
            w.Write(port);
            w.Write(token ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a transfer event body.</summary>
        public static (string zoneId, string host, int port, string token) DecodeTransfer(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return (r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadString());
        }
    }

    // ---- client ----

    /// <summary>
    /// Client-side zone driver, attached by <see cref="ZonesClientExtensions.UseZones"/>. When the current node
    /// decides to move you to another zone it raises <see cref="TransferRequested"/> with the destination node's
    /// address and a one-time token; the app connects a client to that node and calls <see cref="ClaimAsync"/> with
    /// the token to receive the state the origin carried across — giving a seamless handoff with no re-login. Rides
    /// the unified protocol on the <see cref="Channels.Zones"/> channel.
    /// </summary>
    public sealed class ZonesClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised when the server instructs this client to migrate to another node/zone.</summary>
        public event Action<ZoneTransfer>? TransferRequested;

        internal ZonesClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Zones, (ushort)ZoneEvt.Transfer, OnTransferEvent));
        }

        /// <summary>
        /// Claims a handoff token on the node this client is connected to (the destination). Returns the opaque state
        /// the origin node carried across. Call after connecting to the destination named in a <see cref="ZoneTransfer"/>.
        /// </summary>
        public async Task<byte[]> ClaimAsync(string token)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Zones, (ushort)ZoneOp.Claim, ZoneWire.EncodeClaim(token)).ConfigureAwait(false);
                var (_, state) = ZoneWire.DecodeClaimReply(body);
                return state;
            }
            catch (ProtocolException ex) { throw new ZoneException(ex.Message); }
            catch (TimeoutException) { throw new ZoneException("Zone claim timed out."); }
        }

        private void OnTransferEvent(byte[] body)
        {
            var (zoneId, host, port, token) = ZoneWire.DecodeTransfer(body);
            TransferRequested?.Invoke(new ZoneTransfer
            {
                Target = new ZoneTarget(zoneId, host, port),
                Token = token,
            });
        }
    }

    // ---- server ----

    /// <summary>
    /// Server-side zone handoff hub, attached by <see cref="ZonesServerExtensions.UseZones"/>. Game logic calls
    /// <see cref="TransferAsync"/> to move a connected player to another node: the player's carried state is stashed
    /// in the (shared) <see cref="IHandoffStore"/> under a one-time token and a migrate instruction is pushed to the
    /// client. The destination node — running the same hub over the same store — hands that state back on claim.
    /// </summary>
    public sealed class ZonesServer
    {
        private static readonly ConcurrentDictionary<BaseServer, ZonesServer> Servers = new ConcurrentDictionary<BaseServer, ZonesServer>();

        private readonly IHandoffStore _store;
        private readonly ZoneOptions _options;

        internal ZonesServer(IHandoffStore store, ZoneOptions options)
        {
            _store = store;
            _options = options;
        }

        internal static ZonesServer Enable(BaseServer server, IHandoffStore? store, ZoneOptions? options)
            => Servers.GetOrAdd(server, _ => new ZonesServer(store ?? new MemoryHandoffStore(), options ?? new ZoneOptions()));

        internal static ZonesServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Resolves the stable player key for a connected peer (per the configured resolver).</summary>
        public string KeyOf(BasePeer peer) => _options.PlayerKey(peer);

        /// <summary>
        /// Transfers <paramref name="peer"/> to <paramref name="target"/>, carrying <paramref name="carryState"/> across.
        /// Stashes the state under a one-time token and pushes the client a migrate instruction; returns the token.
        /// The destination node must share the same <see cref="IHandoffStore"/> to complete the claim.
        /// </summary>
        public async Task<string> TransferAsync(BasePeer peer, ZoneTarget target, byte[] carryState)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var token = Guid.NewGuid().ToString("N");
            var handoff = new ZoneHandoff { PlayerKey = _options.PlayerKey(peer), ZoneId = target.ZoneId, State = carryState ?? Array.Empty<byte>() };
            await _store.PutAsync(token, handoff, _options.HandoffTtl).ConfigureAwait(false);

            try { await peer.PublishRawAsync(Channels.Zones, (ushort)ZoneEvt.Transfer, ZoneWire.EncodeTransfer(target.ZoneId, target.Host, target.Port, token)).ConfigureAwait(false); }
            catch { /* client dropped; the token simply expires */ }
            return token;
        }

        internal async Task OnClaim(ChannelRequest request, string token)
        {
            var handoff = await _store.TakeAsync(token).ConfigureAwait(false);
            if (handoff == null) throw new ProtocolException("Unknown or expired handoff token.");
            await request.ReplyRawAsync(ZoneWire.EncodeClaimReply(handoff.ZoneId, handoff.State)).ConfigureAwait(false);
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>
    /// Auto-discovered channel service for zone claim commands. Replaces the former hand-framed
    /// <c>[MessageHandler]</c> classes and correlation plumbing: the unified protocol handles correlation and reply
    /// framing, so this only implements the claim logic and dispatches on the op.
    /// </summary>
    [ProtocolChannel(Channels.Zones)]
    public sealed class ZonesChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = ZonesServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("zones are not configured on this server");

            switch ((ZoneOp)request.Op)
            {
                case ZoneOp.Claim:
                    return hub.OnClaim(request, ZoneWire.DecodeClaim(request.RawBody));
                default:
                    return Task.CompletedTask;
            }
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the zone handoff hub to a server by composition.</summary>
    public static class ZonesServerExtensions
    {
        /// <summary>Enables the server-side zone hub; returns it so game logic can transfer players. Share the same <see cref="IHandoffStore"/> across nodes.</summary>
        public static ZonesServer UseZones(this BaseServer server, IHandoffStore? store = null, ZoneOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return ZonesServer.Enable(server, store, options);
        }
    }

    /// <summary>Attaches a zone driver to a client by composition.</summary>
    public static class ZonesClientExtensions
    {
        /// <summary>Enables client-side zone handoff; returns the driver (<c>TransferRequested</c> event + <c>ClaimAsync</c>).</summary>
        public static ZonesClient UseZones(this BaseClient client) => new ZonesClient(client);
    }

    /// <summary>One-time bootstrap so the zone channel service is discovered. Call at startup.</summary>
    public static class ZonesRuntime
    {
        /// <summary>Ensures the zone layer is discoverable.</summary>
        public static void Enable() { _ = typeof(ZonesChannelService); }
    }
}
