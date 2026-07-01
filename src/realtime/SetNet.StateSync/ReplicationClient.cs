using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>
    /// A client-side replicated entity. Its field values are the interpolated result of the buffered snapshots, so game
    /// code (or a Unity component) reads smooth positions/rotations via the typed getters. Call
    /// <see cref="ClientReplication.Update"/> once per frame to advance interpolation.
    /// </summary>
    public sealed class NetworkEntityView
    {
        /// <summary>The entity's stable network id.</summary>
        public uint NetId { get; }
        /// <summary>The archetype id (selects the schema, and on the client which prefab to instantiate).</summary>
        public ushort ArchetypeId { get; }
        /// <summary>Whether this client owns the entity (eligible for local prediction).</summary>
        public bool IsOwner { get; internal set; }

        internal readonly ReplicaSchema Schema;
        internal readonly List<(double t, FieldValue[] v)> Buffer = new List<(double, FieldValue[])>();
        internal FieldValue[] Latest;
        private FieldValue[] _current;

        internal NetworkEntityView(uint netId, ushort archetype, bool isOwner, ReplicaSchema schema, FieldValue[] initial)
        {
            NetId = netId;
            ArchetypeId = archetype;
            IsOwner = isOwner;
            Schema = schema;
            Latest = initial;
            _current = initial;
        }

        internal void Push(double time, FieldValue[] values)
        {
            Latest = values;
            Buffer.Add((time, values));
            if (Buffer.Count > 64) Buffer.RemoveRange(0, Buffer.Count - 64);
        }

        /// <summary>Recomputes the interpolated <c>current</c> values for the given render time (or snaps to the latest when interpolation is off).</summary>
        internal void Sample(double renderTime, bool snap)
        {
            if (snap || Buffer.Count == 0) { _current = Latest; return; }
            if (Buffer.Count == 1) { _current = Buffer[0].v; return; }

            // Find the two samples bracketing renderTime.
            for (var i = Buffer.Count - 1; i >= 0; i--)
            {
                if (Buffer[i].t <= renderTime)
                {
                    if (i == Buffer.Count - 1) { _current = Buffer[i].v; return; }   // render time is ahead of newest → hold newest
                    var a = Buffer[i];
                    var b = Buffer[i + 1];
                    var span = b.t - a.t;
                    var frac = span <= 0 ? 1f : (float)((renderTime - a.t) / span);
                    if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
                    _current = InterpolateFields(a.v, b.v, frac);
                    return;
                }
            }
            _current = Buffer[0].v;   // render time is behind oldest → hold oldest
        }

        private FieldValue[] InterpolateFields(FieldValue[] a, FieldValue[] b, float frac)
        {
            var n = Schema.Fields.Count;
            var result = new FieldValue[n];
            for (var i = 0; i < n; i++)
            {
                var def = Schema.Fields[i];
                result[i] = def.Interpolate ? FieldValue.Interpolate(a[i], b[i], frac, def.Type) : b[i];
            }
            return result;
        }

        /// <summary>Reads a boolean field.</summary>
        public bool GetBool(int index) => _current[index].Num != 0;
        /// <summary>Reads an integer field.</summary>
        public long GetInt(int index) => (long)_current[index].Num;
        /// <summary>Reads a float/double field.</summary>
        public double GetFloat(int index) => _current[index].Num;
        /// <summary>Reads a 2D vector field.</summary>
        public Vec2 GetVec2(int index) => _current[index].AsVec2();
        /// <summary>Reads a 3D vector field.</summary>
        public Vec3 GetVec3(int index) => _current[index].V3;
        /// <summary>Reads a quaternion field.</summary>
        public Quat GetQuat(int index) => _current[index].Q;
        /// <summary>Reads a string field.</summary>
        public string GetString(int index) => _current[index].Str ?? "";
    }

    /// <summary>
    /// The client-side replication driver, returned by <see cref="ReplicationClientExtensions.UseStateSync"/>. It applies
    /// spawns/despawns and delta snapshots, reconstructs full entity state, and interpolates it. Subscribe to
    /// <see cref="EntitySpawned"/>/<see cref="EntityDespawned"/> to instantiate/destroy your views (or Unity prefabs), and
    /// call <see cref="Update"/> each frame.
    /// </summary>
    public sealed class ClientReplication
    {
        private readonly BaseClient _client;
        private readonly StateSyncOptions _options;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentDictionary<uint, NetworkEntityView> _views = new ConcurrentDictionary<uint, NetworkEntityView>();
        private readonly Dictionary<int, Dictionary<uint, FieldValue[]>> _history = new Dictionary<int, Dictionary<uint, FieldValue[]>>();
        private int _latestTick = -1;
        private uint _inputSeq;

        /// <summary>The last input sequence the server has processed for this client (use it to discard acknowledged inputs during reconciliation).</summary>
        public uint LastProcessedInput { get; private set; }

        /// <summary>Raised when an entity enters this client's view (instantiate your object/prefab here).</summary>
        public event Action<NetworkEntityView>? EntitySpawned;

        /// <summary>Raised when an entity leaves this client's view (destroy your object here).</summary>
        public event Action<NetworkEntityView>? EntityDespawned;

        internal ClientReplication(BaseClient client, StateSyncOptions options)
        {
            _client = client;
            _options = options;
            StateSyncClientRegistry.Register(this);
        }

        /// <summary>All entities currently visible to this client.</summary>
        public IEnumerable<NetworkEntityView> Entities => _views.Values;

        /// <summary>The entity this client owns (for prediction), or null.</summary>
        public NetworkEntityView? OwnedEntity
        {
            get { foreach (var v in _views.Values) if (v.IsOwner) return v; return null; }
        }

        /// <summary>Sends an input command for the owned entity; returns the sequence number stamped on it (echoed back via <see cref="LastProcessedInput"/>).</summary>
        public uint SendInput(byte[] payload)
        {
            var seq = unchecked(++_inputSeq);
            _ = SafeSend(StateSyncTypes.Input, Wire.EncodeInput(seq, payload ?? Array.Empty<byte>()), DeliveryMethod.Unreliable);
            return seq;
        }

        /// <summary>Advances interpolation. Call once per frame (e.g. in Unity's <c>Update</c>).</summary>
        public void Update()
        {
            var snap = _options.InterpolationDelayMs <= 0;
            var renderTime = _clock.Elapsed.TotalSeconds - _options.InterpolationDelayMs / 1000.0;
            lock (_gate)
                foreach (var v in _views.Values) v.Sample(renderTime, snap);
        }

        internal void OnSpawn(uint netId, ushort archetype, bool isOwner, FieldValue[] values)
        {
            var schema = ReplicaRegistry.Get(archetype);
            NetworkEntityView view;
            lock (_gate)
            {
                if (_views.TryGetValue(netId, out var existing)) { existing.IsOwner = isOwner; return; }
                view = new NetworkEntityView(netId, archetype, isOwner, schema, values);
                view.Push(_clock.Elapsed.TotalSeconds, values);
                _views[netId] = view;
            }
            EntitySpawned?.Invoke(view);
        }

        internal void OnDespawn(uint netId)
        {
            NetworkEntityView? view;
            lock (_gate) { _views.TryRemove(netId, out view); }
            if (view != null) EntityDespawned?.Invoke(view);
        }

        internal void OnSnapshot(byte[] frame)
        {
            var spawned = new List<NetworkEntityView>();
            int tick;
            lock (_gate)
            {
                using var ms = new MemoryStream(frame);
                using var r = new BinaryReader(ms, Encoding.UTF8);
                tick = r.ReadInt32();
                var baselineTick = r.ReadInt32();
                LastProcessedInput = r.ReadUInt32();
                var count = r.ReadUInt16();

                if (tick <= _latestTick) return;   // stale/out-of-order snapshot — newer already applied
                _latestTick = tick;

                var baseline = baselineTick >= 0 && _history.TryGetValue(baselineTick, out var b) ? b : null;
                var full = new Dictionary<uint, FieldValue[]>(count);
                var now = _clock.Elapsed.TotalSeconds;

                for (var i = 0; i < count; i++)
                {
                    var netId = r.ReadUInt32();
                    var flags = r.ReadByte();
                    FieldValue[] values;
                    ReplicaSchema schema;
                    bool isOwner = false;
                    bool isNew = false;

                    if ((flags & 1) != 0)
                    {
                        var archetype = r.ReadUInt16();
                        isOwner = r.ReadBoolean();
                        schema = ReplicaRegistry.Get(archetype);
                        values = Wire.ReadAllFields(r, schema);
                        if (!_views.TryGetValue(netId, out var v))
                        {
                            v = new NetworkEntityView(netId, archetype, isOwner, schema, values);
                            _views[netId] = v;
                            spawned.Add(v);
                            isNew = true;
                        }
                        else { v.IsOwner = isOwner; }
                        _ = isNew;
                    }
                    else
                    {
                        if (!_views.TryGetValue(netId, out var v)) return;   // delta for an unknown entity — can't parse the rest; drop
                        schema = v.Schema;
                        var prev = baseline != null && baseline.TryGetValue(netId, out var pv) ? pv : DefaultValues(schema);
                        values = Wire.ReadMaskedFields(r, schema, prev);
                    }

                    full[netId] = values;
                    if (_views.TryGetValue(netId, out var view)) view.Push(now, values);
                }

                _history[tick] = full;
                PruneHistory(tick);
            }

            foreach (var v in spawned) EntitySpawned?.Invoke(v);
            _ = SafeSend(StateSyncTypes.Ack, Wire.EncodeAck(tick), DeliveryMethod.Unreliable);
        }

        private static FieldValue[] DefaultValues(ReplicaSchema schema)
        {
            var v = new FieldValue[schema.Fields.Count];
            for (var i = 0; i < v.Length; i++) v[i] = FieldValue.Default(schema.Fields[i].Type);
            return v;
        }

        private void PruneHistory(int tick)
        {
            var oldest = tick - _options.MaxSnapshotHistory;
            if (oldest <= 0) return;
            List<int>? drop = null;
            foreach (var t in _history.Keys) if (t < oldest) (drop ??= new List<int>()).Add(t);
            if (drop != null) foreach (var t in drop) _history.Remove(t);
        }

        private async Task SafeSend(ushort type, byte[] data, DeliveryMethod delivery)
        {
            try { await _client.SendAsync(type, data, delivery).ConfigureAwait(false); } catch { /* connection dropping */ }
        }
    }

    /// <summary>Process-wide set of client replication drivers that server-push messages are routed to (typically one).</summary>
    internal static class StateSyncClientRegistry
    {
        private static readonly ConcurrentDictionary<ClientReplication, byte> Clients = new ConcurrentDictionary<ClientReplication, byte>();
        public static void Register(ClientReplication client) => Clients[client] = 0;
        public static void ForEach(Action<ClientReplication> action) { foreach (var c in Clients.Keys) action(c); }
    }

    /// <summary>Enables state replication on a client. Register your archetypes and call <see cref="StateSyncRuntime.Enable"/> at startup.</summary>
    public static class ReplicationClientExtensions
    {
        /// <summary>Turns on state replication for a client and returns the driver (entities, spawn/despawn events, input, Update).</summary>
        public static ClientReplication UseStateSync(this BaseClient client, StateSyncOptions? options = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new ClientReplication(client, options ?? new StateSyncOptions());
        }
    }

    /// <summary>Auto-discovered client handler for entity spawns.</summary>
    [MessageHandler(StateSyncTypes.Spawn)]
    public sealed class StateSyncSpawnHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var (netId, archetype, isOwner, values) = Wire.DecodeSpawn(data);
            StateSyncClientRegistry.ForEach(c => c.OnSpawn(netId, archetype, isOwner, values));
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for entity despawns.</summary>
    [MessageHandler(StateSyncTypes.Despawn)]
    public sealed class StateSyncDespawnHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var netId = Wire.DecodeDespawn(data);
            StateSyncClientRegistry.ForEach(c => c.OnDespawn(netId));
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for world snapshots.</summary>
    [MessageHandler(StateSyncTypes.Snapshot)]
    public sealed class StateSyncSnapshotHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            StateSyncClientRegistry.ForEach(c => c.OnSnapshot(data));
            return Task.CompletedTask;
        }
    }
}
