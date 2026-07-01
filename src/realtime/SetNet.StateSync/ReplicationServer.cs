using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.StateSync
{
    /// <summary>Per-observer replication bookkeeping: what that client has spawned, its acknowledged baseline tick, sent-snapshot history, and last input.</summary>
    internal sealed class ObserverState
    {
        public int AckedTick = -1;
        public uint LastInputSeq;
        public readonly HashSet<uint> Known = new HashSet<uint>();
        public readonly Dictionary<int, Dictionary<uint, FieldValue[]>> History = new Dictionary<int, Dictionary<uint, FieldValue[]>>();
    }

    /// <summary>
    /// The server-side replication world, returned by <see cref="ReplicationServerExtensions.UseStateSync"/>. Spawn and
    /// despawn entities, add/remove observers (the peers that receive the world), and receive their inputs. A fixed-rate
    /// tick streams delta-compressed snapshots to each observer over the unreliable channel, with spawns/despawns sent
    /// reliably. The game mutates entity fields whenever; the tick samples them.
    /// </summary>
    public sealed class ServerReplication : IDisposable
    {
        private readonly BaseServer _server;
        private readonly StateSyncOptions _options;
        private readonly ConcurrentDictionary<uint, NetworkEntity> _entities = new ConcurrentDictionary<uint, NetworkEntity>();
        private readonly ConcurrentDictionary<BasePeer, ObserverState> _observers = new ConcurrentDictionary<BasePeer, ObserverState>();
        private long _nextNetId;
        private int _tick;
        private int _ticking;
        private Timer? _timer;

        /// <summary>Raised when an observer sends an input command (args: the peer, its input sequence number, and the raw payload you sent from the client).</summary>
        public event Action<BasePeer, uint, byte[]>? InputReceived;

        internal ServerReplication(BaseServer server, StateSyncOptions options)
        {
            _server = server;
            _options = options;
            _server.PeerDisconnected += peer => RemoveObserver(peer);
            if (options.AutoObserve) _server.PeerConnected += peer => AddObserver(peer);
            var period = Math.Max(10, 1000 / Math.Max(1, options.TickRate));
            _timer = new Timer(_ => _ = TickAsync(), null, period, period);
        }

        /// <summary>Creates a new replicated entity of the given archetype, optionally owned by a peer. Mutate its fields via the typed setters.</summary>
        public NetworkEntity Spawn(ushort archetype, Guid owner = default)
        {
            var schema = ReplicaRegistry.Get(archetype);
            var netId = (uint)Interlocked.Increment(ref _nextNetId);
            var entity = new NetworkEntity(netId, archetype, owner, schema);
            _entities[netId] = entity;
            return entity;
        }

        /// <summary>Removes an entity from the world; it will be despawned on every client that can see it.</summary>
        public void Despawn(NetworkEntity entity)
        {
            if (entity == null) return;
            _entities.TryRemove(entity.NetId, out _);
            // Clients are told on the next tick (the entity simply stops being visible); do an immediate reliable despawn too.
            foreach (var kv in _observers)
            {
                if (kv.Value.Known.Remove(entity.NetId))
                    _ = SafeSend(kv.Key, StateSyncTypes.Despawn, Wire.EncodeDespawn(entity.NetId), DeliveryMethod.Reliable);
            }
        }

        /// <summary>Starts replicating the world to a peer. Call once the peer is ready to receive the game (e.g. after it joins).</summary>
        public void AddObserver(BasePeer peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            _observers[peer] = new ObserverState();
        }

        /// <summary>Stops replicating to a peer (also called automatically when the peer disconnects).</summary>
        public void RemoveObserver(BasePeer peer) => _observers.TryRemove(peer, out _);

        internal void OnAck(BasePeer peer, int tick)
        {
            if (_observers.TryGetValue(peer, out var state) && tick > state.AckedTick)
                state.AckedTick = tick;
        }

        internal void OnInput(BasePeer peer, uint seq, byte[] payload)
        {
            if (_observers.TryGetValue(peer, out var state) && seq > state.LastInputSeq)
                state.LastInputSeq = seq;
            InputReceived?.Invoke(peer, seq, payload);
        }

        private async Task TickAsync()
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;   // never overlap ticks
            try
            {
                var tick = ++_tick;
                var all = new List<NetworkEntity>(_entities.Values);
                foreach (var kv in _observers)
                {
                    try { await SendToObserverAsync(kv.Key, kv.Value, tick, all).ConfigureAwait(false); }
                    catch { /* one observer failing must not stall the tick */ }
                }
            }
            catch { /* never throw on the timer thread */ }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        private async Task SendToObserverAsync(BasePeer peer, ObserverState state, int tick, List<NetworkEntity> all)
        {
            var ownerId = peer.CurrentPeerInfo.Id;

            // Resolve the visible set for this observer.
            var visible = new Dictionary<uint, NetworkEntity>();
            foreach (var e in _options.Interest.Query(peer, all)) visible[e.NetId] = e;

            // Spawn newly-visible entities (reliable) and despawn ones that left the interest set (reliable).
            foreach (var e in visible.Values)
            {
                if (state.Known.Add(e.NetId))
                    await SafeSend(peer, StateSyncTypes.Spawn,
                        Wire.EncodeSpawn(e.NetId, e.ArchetypeId, e.Owner != Guid.Empty && e.Owner == ownerId, e.Schema, e.Values),
                        DeliveryMethod.Reliable).ConfigureAwait(false);
            }
            if (state.Known.Count > visible.Count)
            {
                var gone = new List<uint>();
                foreach (var knownId in state.Known) if (!visible.ContainsKey(knownId)) gone.Add(knownId);
                foreach (var id in gone)
                {
                    state.Known.Remove(id);
                    await SafeSend(peer, StateSyncTypes.Despawn, Wire.EncodeDespawn(id), DeliveryMethod.Reliable).ConfigureAwait(false);
                }
            }

            // Build the delta snapshot against the observer's acknowledged baseline.
            var ackedTick = state.AckedTick;
            var baseline = ackedTick >= 0 && state.History.TryGetValue(ackedTick, out var b) ? b : null;
            var currentFull = new Dictionary<uint, FieldValue[]>(visible.Count);

            byte[] frame;
            using (var ms = new MemoryStream())
            {
                using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
                w.Write(tick);
                w.Write(baseline != null ? ackedTick : -1);
                w.Write(state.LastInputSeq);
                w.Write((ushort)visible.Count);

                foreach (var e in visible.Values)
                {
                    var cur = e.Snapshot();
                    currentFull[e.NetId] = cur;
                    w.Write(e.NetId);

                    if (baseline != null && baseline.TryGetValue(e.NetId, out var prev))
                    {
                        w.Write((byte)0);   // delta
                        Wire.WriteMaskedFields(w, e.Schema, prev, cur);
                    }
                    else
                    {
                        w.Write((byte)1);   // full (new to the baseline)
                        w.Write(e.ArchetypeId);
                        w.Write(e.Owner != Guid.Empty && e.Owner == ownerId);
                        Wire.WriteAllFields(w, e.Schema, cur);
                    }
                }
                w.Flush();
                frame = ms.ToArray();
            }

            // Record and prune history, then send unreliably (a lost snapshot self-heals on the next tick).
            state.History[tick] = currentFull;
            PruneHistory(state, tick);
            await SafeSend(peer, StateSyncTypes.Snapshot, frame, DeliveryMethod.Unreliable).ConfigureAwait(false);
        }

        private void PruneHistory(ObserverState state, int tick)
        {
            var oldest = tick - _options.MaxSnapshotHistory;
            if (oldest <= 0) return;
            List<int>? drop = null;
            foreach (var t in state.History.Keys)
                if (t < oldest && t != state.AckedTick) (drop ??= new List<int>()).Add(t);
            if (drop != null) foreach (var t in drop) state.History.Remove(t);
        }

        private static async Task SafeSend(BasePeer peer, ushort type, byte[] data, DeliveryMethod delivery)
        {
            try { await peer.SendAsync(type, data, delivery).ConfigureAwait(false); } catch { /* peer dropping */ }
        }

        /// <inheritdoc/>
        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>Enables state replication on a server. Register your archetypes (<see cref="ReplicaRegistry"/>) and call <see cref="StateSyncRuntime.Enable"/> at startup.</summary>
    public static class ReplicationServerExtensions
    {
        private static readonly ConcurrentDictionary<BaseServer, ServerReplication> Servers
            = new ConcurrentDictionary<BaseServer, ServerReplication>();

        /// <summary>Turns on state replication for a server and returns the world handle (spawn/despawn/observers/input).</summary>
        public static ServerReplication UseStateSync(this BaseServer server, StateSyncOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            var world = new ServerReplication(server, options ?? new StateSyncOptions());
            Servers[server] = world;
            return world;
        }

        internal static ServerReplication? Get(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var w) ? w : null;
    }

    /// <summary>Auto-discovered server handler for snapshot acknowledgements.</summary>
    [MessageHandler(StateSyncTypes.Ack)]
    public sealed class StateSyncAckHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            ReplicationServerExtensions.Get(peer.CurrentPeerInfo.Server)?.OnAck(peer, Wire.DecodeAck(data));
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered server handler for client input commands.</summary>
    [MessageHandler(StateSyncTypes.Input)]
    public sealed class StateSyncInputHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var world = ReplicationServerExtensions.Get(peer.CurrentPeerInfo.Server);
            if (world != null) { var (seq, payload) = Wire.DecodeInput(data); world.OnInput(peer, seq, payload); }
            return Task.CompletedTask;
        }
    }
}
