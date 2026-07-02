using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Zones
{
    /// <summary>Reserved wire types for the zone handoff service. Don't reuse these ids for application messages.</summary>
    public static class ZoneTypes
    {
        /// <summary>Client → server: claim a handoff token on the destination node.</summary>
        public const ushort Command = ushort.MaxValue - 55;   // 65480

        /// <summary>Server → client: correlated reply to a claim (the carried state).</summary>
        public const ushort Reply = ushort.MaxValue - 56;     // 65479

        /// <summary>Server → client: push event instructing the client to migrate to another node.</summary>
        public const ushort Event = ushort.MaxValue - 57;     // 65478
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

    internal sealed class ZoneClaimReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string ZoneId = "";
        public byte[] State = Array.Empty<byte>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(ZoneId ?? "");
            w.Write(State?.Length ?? 0);
            if (State != null) w.Write(State);
            return ms.ToArray();
        }

        public static ZoneClaimReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new ZoneClaimReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
                ZoneId = r.ReadString(),
            };
            var len = r.ReadInt32();
            reply.State = len > 0 ? r.ReadBytes(len) : Array.Empty<byte>();
            return reply;
        }
    }

    internal sealed class ZoneTransferEvent
    {
        public string ZoneId = "";
        public string Host = "";
        public int Port;
        public string Token = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ZoneId ?? "");
            w.Write(Host ?? "");
            w.Write(Port);
            w.Write(Token ?? "");
            return ms.ToArray();
        }

        public static ZoneTransferEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new ZoneTransferEvent
            {
                ZoneId = r.ReadString(),
                Host = r.ReadString(),
                Port = r.ReadInt32(),
                Token = r.ReadString(),
            };
        }
    }

    // ---- client ----

    internal static class ZoneRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<ZoneClaimReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<ZoneClaimReply>>();
        private static readonly ConcurrentDictionary<ZonesClient, byte> Clients
            = new ConcurrentDictionary<ZonesClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<ZoneClaimReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, ZoneClaimReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(ZonesClient c) => Clients[c] = 0;
        public static void DispatchEvent(ZoneTransferEvent evt) { foreach (var c in Clients.Keys) c.OnTransfer(evt); }
    }

    /// <summary>
    /// Client-side zone driver, attached by <see cref="ZonesClientExtensions.UseZones"/>. When the current node
    /// decides to move you to another zone it raises <see cref="TransferRequested"/> with the destination node's
    /// address and a one-time token; the app connects a client to that node and calls <see cref="ClaimAsync"/> with
    /// the token to receive the state the origin carried across — giving a seamless handoff with no re-login.
    /// </summary>
    public sealed class ZonesClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when the server instructs this client to migrate to another node/zone.</summary>
        public event Action<ZoneTransfer>? TransferRequested;

        internal ZonesClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ZoneRegistry.RegisterClient(this);
        }

        /// <summary>
        /// Claims a handoff token on the node this client is connected to (the destination). Returns the opaque state
        /// the origin node carried across. Call after connecting to the destination named in a <see cref="ZoneTransfer"/>.
        /// </summary>
        public async Task<byte[]> ClaimAsync(string token)
        {
            var id = ZoneRegistry.NextId();
            var tcs = new TaskCompletionSource<ZoneClaimReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            ZoneRegistry.Register(id, tcs);
            try
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) { w.Write(id); w.Write(token ?? ""); }
                await _client.SendAsync(ZoneTypes.Command, ms.ToArray(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    ZoneClaimReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new ZoneException("Zone claim timed out."); }
                    if (!reply.Success) throw new ZoneException(reply.Error);
                    return reply.State;
                }
            }
            finally { ZoneRegistry.Remove(id); }
        }

        internal void OnTransfer(ZoneTransferEvent evt)
            => TransferRequested?.Invoke(new ZoneTransfer
            {
                Target = new ZoneTarget(evt.ZoneId, evt.Host, evt.Port),
                Token = evt.Token,
            });
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

            var evt = new ZoneTransferEvent { ZoneId = target.ZoneId, Host = target.Host, Port = target.Port, Token = token };
            try { await peer.SendAsync(ZoneTypes.Event, evt.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false); }
            catch { /* client dropped; the token simply expires */ }
            return token;
        }

        internal async Task OnClaim(BasePeer peer, int correlationId, string token)
        {
            var handoff = await _store.TakeAsync(token).ConfigureAwait(false);
            ZoneClaimReply reply = handoff == null
                ? new ZoneClaimReply { CorrelationId = correlationId, Success = false, Error = "Unknown or expired handoff token." }
                : new ZoneClaimReply { CorrelationId = correlationId, Success = true, ZoneId = handoff.ZoneId, State = handoff.State };
            try { await peer.SendAsync(ZoneTypes.Reply, reply.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropped */ }
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for zone claim commands.</summary>
    [MessageHandler(ZoneTypes.Command)]
    public sealed class ZoneCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = ZonesServer.For(peer.CurrentPeerInfo.Server);
            if (hub == null) return Task.CompletedTask;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32();
            var token = r.ReadString();
            return hub.OnClaim(peer, corr, token);
        }
    }

    /// <summary>Auto-discovered client handler for correlated zone claim replies.</summary>
    [MessageHandler(ZoneTypes.Reply)]
    public sealed class ZoneReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = ZoneClaimReply.Decode(data); ZoneRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for zone migrate instructions.</summary>
    [MessageHandler(ZoneTypes.Event)]
    public sealed class ZoneEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { ZoneRegistry.DispatchEvent(ZoneTransferEvent.Decode(data)); return Task.CompletedTask; }
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

    /// <summary>One-time bootstrap so the zone handlers are discovered. Call at startup.</summary>
    public static class ZonesRuntime
    {
        /// <summary>Ensures the zone layer is discoverable.</summary>
        public static void Enable() { _ = ZoneTypes.Command; }
    }
}
