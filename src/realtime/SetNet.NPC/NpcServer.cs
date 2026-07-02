using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.NPC
{
    /// <summary>
    /// Server-side NPC hub, attached by <see cref="NpcServerExtensions.UseNpc"/>. Owns the behaviour registry and the
    /// live instance registry, resolves interest, gates interactions via <see cref="NpcOptions.CanInteract"/>, and
    /// pushes spawn/despawn to interested clients. Game logic registers one <see cref="INpcBehaviour"/> per NPC type
    /// and spawns instances; the interact round-trip is uniform. Rides the unified protocol on the
    /// <see cref="Channels.Npc"/> channel — no per-module wire types.
    /// </summary>
    public sealed class NpcServer
    {
        private static readonly ConcurrentDictionary<BaseServer, NpcServer> Servers = new ConcurrentDictionary<BaseServer, NpcServer>();

        private readonly BaseServer _server;
        private readonly NpcOptions _options;
        private readonly IServiceProvider _services;

        private readonly ConcurrentDictionary<string, INpcBehaviour> _behaviours = new ConcurrentDictionary<string, INpcBehaviour>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, NpcInstance> _instances = new ConcurrentDictionary<string, NpcInstance>(StringComparer.Ordinal);

        /// <summary>Online peers, and the zone each is currently subscribed to (null = none).</summary>
        private readonly ConcurrentDictionary<Guid, PeerSub> _peers = new ConcurrentDictionary<Guid, PeerSub>();

        private sealed class PeerSub
        {
            public BasePeer Peer = null!;
            public string? Zone;
        }

        internal NpcServer(BaseServer server, NpcOptions options)
        {
            _server = server;
            _options = options;
            _services = options.Services ?? EmptyServiceProvider.Instance;
        }

        internal static NpcServer Enable(BaseServer server, NpcOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new NpcServer(s, options ?? new NpcOptions());
                s.PeerConnected += peer => hub._peers[peer.CurrentPeerInfo.Id] = new PeerSub { Peer = peer, Zone = null };
                s.PeerDisconnected += peer => hub._peers.TryRemove(peer.CurrentPeerInfo.Id, out _);
                return hub;
            });

        /// <summary>Resolves the NPC hub configured on a server (or null when <c>UseNpc</c> was never called).</summary>
        public static NpcServer? Get(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var hub) ? hub : null;

        /// <summary>The interest policy in effect (default <see cref="AllInterest"/>).</summary>
        public INpcInterest Interest => _options.Interest;

        /// <summary>The pre-interaction gate (player key + instance → allow). Default: always allow.</summary>
        public Func<string, NpcInstance, bool> CanInteract => _options.CanInteract;

        /// <summary>Resolves the stable player key for a connected peer (per the configured resolver).</summary>
        public string KeyOf(BasePeer peer) => _options.PlayerKey(peer);

        // ---- registration ----

        /// <summary>Registers a behaviour (one per <see cref="INpcBehaviour.NpcType"/>); a later registration for the same type replaces it.</summary>
        public void Register(INpcBehaviour behaviour)
        {
            if (behaviour == null) throw new ArgumentNullException(nameof(behaviour));
            if (string.IsNullOrEmpty(behaviour.NpcType)) throw new ArgumentException("NpcType must be set.", nameof(behaviour));
            _behaviours[behaviour.NpcType] = behaviour;
        }

        /// <summary>Looks up a registered behaviour by type, or null.</summary>
        public INpcBehaviour? BehaviourFor(string type)
            => type != null && _behaviours.TryGetValue(type, out var b) ? b : null;

        // ---- spawn / despawn ----

        /// <summary>
        /// Spawns an instance and pushes it to interested clients. Returns the instance id. Throws if the type has no
        /// registered behaviour. Fires the behaviour's <see cref="INpcBehaviour.OnSpawnAsync"/> hook (errors isolated).
        /// </summary>
        public string Spawn(NpcSpawn spawn)
        {
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            if (string.IsNullOrEmpty(spawn.Type)) throw new ArgumentException("NpcSpawn.Type must be set.", nameof(spawn));
            if (!_behaviours.ContainsKey(spawn.Type))
                throw new InvalidOperationException($"No NPC behaviour registered for type '{spawn.Type}'.");

            var id = spawn.Id ?? Guid.NewGuid().ToString("N");
            var instance = new NpcInstance(id, spawn.Type, spawn.Position, spawn.Zone, spawn.Metadata);
            _instances[id] = instance;

            // Fire OnSpawn (no interacting peer at spawn time) — best-effort.
            if (_behaviours.TryGetValue(spawn.Type, out var behaviour))
                _ = SafeLifecycle(() => behaviour.OnSpawnAsync(new NpcContext(instance, PlaceholderPeer(instance), "", _services)));

            _ = PushToInterestedAsync((ushort)NpcEvt.Spawned, NpcWire.EncodeInstance(instance), instance);
            return id;
        }

        /// <summary>Despawns an instance (if present) and pushes a despawn to interested clients. Returns whether it existed.</summary>
        public bool Despawn(string id)
        {
            if (id == null || !_instances.TryRemove(id, out var instance)) return false;

            if (_behaviours.TryGetValue(instance.Type, out var behaviour))
                _ = SafeLifecycle(() => behaviour.OnDespawnAsync(new NpcContext(instance, PlaceholderPeer(instance), "", _services)));

            _ = PushToInterestedAsync((ushort)NpcEvt.Despawned, NpcWire.EncodeDespawned(instance.Id, instance.Zone), instance);
            return true;
        }

        /// <summary>All currently-spawned instances in a zone (for tooling / respawn / the EnterZone snapshot).</summary>
        public IReadOnlyList<NpcInstance> InstancesInZone(string zone)
        {
            var list = new List<NpcInstance>();
            foreach (var inst in _instances.Values)
                if (string.Equals(inst.Zone, zone, StringComparison.Ordinal)) list.Add(inst);
            return list;
        }

        /// <summary>Looks up a live instance by id, or null.</summary>
        public NpcInstance? InstanceById(string id)
            => id != null && _instances.TryGetValue(id, out var inst) ? inst : null;

        // ---- interaction (called by the channel service) ----

        internal async Task HandleInteractAsync(ChannelRequest request)
        {
            var (npcId, action, payload) = NpcWire.DecodeInteract(request.RawBody);
            var peer = request.Peer;
            var playerKey = _options.PlayerKey(peer);

            if (!_instances.TryGetValue(npcId, out var instance))
            {
                await request.ReplyRawAsync(NpcWire.EncodeResponse(NpcResponse.Fail("NPC not found."))).ConfigureAwait(false);
                return;
            }

            bool allowed;
            try { allowed = _options.CanInteract(playerKey, instance); }
            catch { allowed = false; }
            if (!allowed)
            {
                await request.ReplyRawAsync(NpcWire.EncodeResponse(NpcResponse.Fail("Interaction not allowed."))).ConfigureAwait(false);
                return;
            }

            var behaviour = BehaviourFor(instance.Type);
            if (behaviour == null)
            {
                await request.ReplyRawAsync(NpcWire.EncodeResponse(NpcResponse.Fail($"No behaviour for '{instance.Type}'."))).ConfigureAwait(false);
                return;
            }

            NpcResponse response;
            try
            {
                var ctx = new NpcContext(instance, peer, playerKey, _services);
                response = await behaviour.OnInteractAsync(ctx, new NpcInteraction(action, payload)).ConfigureAwait(false)
                           ?? NpcResponse.Fail("Behaviour returned no response.");
            }
            catch (Exception ex)
            {
                response = NpcResponse.Fail(ex.Message);
            }

            await request.ReplyRawAsync(NpcWire.EncodeResponse(response)).ConfigureAwait(false);
        }

        internal async Task HandleEnterZoneAsync(ChannelRequest request)
        {
            var zone = NpcWire.DecodeZone(request.RawBody);
            var peer = request.Peer;
            if (_peers.TryGetValue(peer.CurrentPeerInfo.Id, out var sub)) sub.Zone = zone;
            else _peers[peer.CurrentPeerInfo.Id] = new PeerSub { Peer = peer, Zone = zone };

            // Reply with the instances this peer is now interested in.
            var visible = new List<NpcInstance>();
            foreach (var inst in _instances.Values)
                if (_options.Interest.IsInterested(peer, zone, inst)) visible.Add(inst);

            await request.ReplyRawAsync(NpcWire.EncodeInstanceList(visible)).ConfigureAwait(false);
        }

        internal void HandleLeaveZone(ChannelRequest request)
        {
            var zone = NpcWire.DecodeZone(request.RawBody);
            if (_peers.TryGetValue(request.Peer.CurrentPeerInfo.Id, out var sub) &&
                string.Equals(sub.Zone, zone, StringComparison.Ordinal))
                sub.Zone = null;
        }

        // ---- interest fan-out ----

        private async Task PushToInterestedAsync(ushort evt, byte[] body, NpcInstance instance)
        {
            foreach (var sub in _peers.Values)
            {
                if (!_options.Interest.IsInterested(sub.Peer, sub.Zone, instance)) continue;
                try { await sub.Peer.PublishRawAsync(Channels.Npc, evt, body).ConfigureAwait(false); }
                catch { /* peer dropping; skip */ }
            }
        }

        private static async Task SafeLifecycle(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); } catch { /* isolate behaviour lifecycle errors */ }
        }

        // The spawn/despawn lifecycle has no interacting peer; behaviours that need a peer use OnInteract. We hand the
        // first online peer (or a throwing-free stand-in) only so NpcContext stays non-null; lifecycle behaviours that
        // touch Peer should guard for spawn/despawn.
        private BasePeer PlaceholderPeer(NpcInstance instance)
        {
            foreach (var sub in _peers.Values) return sub.Peer;
            throw new InvalidOperationException(
                "OnSpawnAsync/OnDespawnAsync ran with no connected peers; a lifecycle behaviour must not rely on NpcContext.Peer.");
        }
    }

    /// <summary>Auto-discovered channel service for NPC commands (interact / enter-zone / leave-zone).</summary>
    [ProtocolChannel(Channels.Npc)]
    public sealed class NpcChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = NpcServer.Get(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("NPCs are not configured on this server");

            switch ((NpcOp)request.Op)
            {
                case NpcOp.Interact: return hub.HandleInteractAsync(request);
                case NpcOp.EnterZone: return hub.HandleEnterZoneAsync(request);
                case NpcOp.LeaveZone:
                    hub.HandleLeaveZone(request);
                    return Task.CompletedTask;
                default:
                    throw new ProtocolException($"unknown NPC op {request.Op}");
            }
        }
    }

    /// <summary>Attaches the NPC hub to a server by composition — no base class.</summary>
    public static class NpcServerExtensions
    {
        /// <summary>Enables the server-side NPC hub; returns it so game logic can register behaviours and spawn instances.</summary>
        public static NpcServer UseNpc(this BaseServer server, NpcOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return NpcServer.Enable(server, options);
        }
    }

    /// <summary>One-time bootstrap so the NPC channel service is discovered. Call at startup.</summary>
    public static class NpcRuntime
    {
        /// <summary>Ensures the NPC layer is discoverable.</summary>
        public static void Enable() { _ = typeof(NpcChannelService); }
    }
}
