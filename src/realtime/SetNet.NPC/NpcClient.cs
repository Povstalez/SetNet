using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.NPC
{
    /// <summary>
    /// Client-side NPC driver, attached by <see cref="NpcClientExtensions.UseNpc"/>. Discovery is push-based: enter a
    /// zone to receive its interest-scoped spawn/despawn stream, keep a live <see cref="Nearby"/> set, and interact by
    /// id. The uniform interact loop returns an <see cref="NpcResponse"/> whose optional
    /// <see cref="NpcResponse.Capability"/> tells the app which existing domain UI to open (vendor/bank/teleport…).
    /// Rides the unified protocol on the <see cref="Channels.Npc"/> channel.
    /// </summary>
    public sealed class NpcClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly ConcurrentDictionary<string, NpcInstance> _nearby = new ConcurrentDictionary<string, NpcInstance>(StringComparer.Ordinal);
        private readonly HashSet<string> _zones = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        /// <summary>Raised when an interest-scoped instance spawns (or is delivered by an <see cref="EnterZoneAsync"/> snapshot).</summary>
        public event Action<NpcInstance>? NpcSpawned;

        /// <summary>Raised when an interest-scoped instance despawns (arg: its id).</summary>
        public event Action<string>? NpcDespawned;

        /// <summary>The instances the client currently knows about (interest-scoped).</summary>
        public IReadOnlyCollection<NpcInstance> Nearby => new List<NpcInstance>(_nearby.Values);

        internal NpcClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Npc, (ushort)NpcEvt.Spawned, OnSpawnedEvent));
            _subscriptions.Add(_client.OnRaw(Channels.Npc, (ushort)NpcEvt.Despawned, OnDespawnedEvent));
        }

        /// <summary>
        /// Interacts with an NPC by id and returns its <see cref="NpcResponse"/>. A server-side failure (unknown NPC,
        /// <c>CanInteract</c> rejection, a behaviour that threw) comes back as a response with <c>Ok=false</c> rather
        /// than an exception; only transport/timeout problems throw <see cref="NpcException"/>.
        /// </summary>
        public async Task<NpcResponse> InteractAsync(string npcId, string action, byte[]? payload = null)
        {
            if (npcId == null) throw new ArgumentNullException(nameof(npcId));
            try
            {
                var body = await _client.RequestRawAsync(Channels.Npc, (ushort)NpcOp.Interact,
                    NpcWire.EncodeInteract(npcId, action, payload)).ConfigureAwait(false);
                return NpcWire.DecodeResponse(body);
            }
            catch (ProtocolException ex) { return NpcResponse.Fail(ex.Message); }
            catch (TimeoutException) { throw new NpcException("NPC interaction timed out."); }
        }

        /// <summary>
        /// Subscribes to a zone's spawn/despawn stream and merges its current instances into <see cref="Nearby"/>
        /// (raising <see cref="NpcSpawned"/> for each). Idempotent.
        /// </summary>
        public async Task EnterZoneAsync(string zone)
        {
            if (zone == null) throw new ArgumentNullException(nameof(zone));
            lock (_gate) _zones.Add(zone);
            try
            {
                var body = await _client.RequestRawAsync(Channels.Npc, (ushort)NpcOp.EnterZone, NpcWire.EncodeZone(zone)).ConfigureAwait(false);
                foreach (var inst in NpcWire.DecodeInstanceList(body)) Add(inst);
            }
            catch (ProtocolException ex) { throw new NpcException(ex.Message); }
            catch (TimeoutException) { throw new NpcException("EnterZone timed out."); }
        }

        /// <summary>Unsubscribes from a zone (fire-and-forget) and drops that zone's instances from <see cref="Nearby"/>.</summary>
        public async Task LeaveZoneAsync(string zone)
        {
            if (zone == null) throw new ArgumentNullException(nameof(zone));
            lock (_gate) _zones.Remove(zone);
            await _client.PostRawAsync(Channels.Npc, (ushort)NpcOp.LeaveZone, NpcWire.EncodeZone(zone)).ConfigureAwait(false);
            foreach (var inst in _nearby.Values)
                if (string.Equals(inst.Zone, zone, StringComparison.Ordinal)) Remove(inst.Id, inst.Zone);
        }

        private void OnSpawnedEvent(byte[] body)
        {
            NpcInstance inst;
            try { inst = NpcWire.DecodeInstance(body); } catch { return; }
            if (!IsSubscribed(inst.Zone)) return;   // not a zone we're watching (co-located client filter)
            Add(inst);
        }

        private void OnDespawnedEvent(byte[] body)
        {
            string id, zone;
            try { (id, zone) = NpcWire.DecodeDespawned(body); } catch { return; }
            if (!IsSubscribed(zone)) return;
            Remove(id, zone);
        }

        private bool IsSubscribed(string zone)
        {
            lock (_gate) return _zones.Count == 0 || _zones.Contains(zone);
        }

        private void Add(NpcInstance inst)
        {
            _nearby[inst.Id] = inst;
            NpcSpawned?.Invoke(inst);
        }

        private void Remove(string id, string zone)
        {
            if (_nearby.TryRemove(id, out _)) NpcDespawned?.Invoke(id);
        }
    }

    /// <summary>Attaches an NPC driver to a client by composition — no base class.</summary>
    public static class NpcClientExtensions
    {
        /// <summary>Enables client-side NPC support; returns the driver (interact + zone subscription + spawn/despawn events).</summary>
        public static NpcClient UseNpc(this BaseClient client) => new NpcClient(client);
    }
}
