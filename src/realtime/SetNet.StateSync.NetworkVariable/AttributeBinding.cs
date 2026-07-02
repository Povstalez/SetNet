using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SetNet.StateSync;

namespace SetNet.StateSync.NetworkVariable
{
    /// <summary>
    /// Marks a field as a replicated network variable. Tag plain fields on a POCO and the framework builds the entity
    /// schema from them and copies values in both directions — no manual field indices.
    /// <code>
    /// [SetNetObject(1)]
    /// public class PlayerState
    /// {
    ///     [SetNetVariable] public int Health = 100;
    ///     [SetNetVariable(Interpolate = true)] public Vec3 Position;
    /// }
    /// </code>
    /// <b>Caveat:</b> a plain field can't self-notify on change, so values are polled via reflection each
    /// <c>Push</c>/<c>Pull</c>. That is fine for a .NET server and non-AOT clients; under Unity IL2CPP or on hot paths
    /// prefer the wrapper <see cref="NetworkVariable{T}"/> (real setter interception, no reflection).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true)]
    public sealed class SetNetVariableAttribute : Attribute
    {
        /// <summary>Whether the client smoothly interpolates this field (float/double/vector/quaternion only).</summary>
        public bool Interpolate { get; set; }

        /// <summary>Quantization step for float/vector fields (e.g. 0.001). 0 = send raw. Ignored for non-float types.</summary>
        public float Precision { get; set; }

        /// <summary>
        /// Explicit field order (lowest first). Leave at -1 to use declaration order (metadata token). Set it when the
        /// client and server compile the type separately and you want a guaranteed stable order.
        /// </summary>
        public int Order { get; set; } = -1;
    }

    /// <summary>Declares the archetype id for a class of <see cref="SetNetVariableAttribute"/> fields, so <c>Register&lt;T&gt;()</c> needs no argument.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class SetNetObjectAttribute : Attribute
    {
        /// <summary>Creates the attribute with the archetype id shared by client and server.</summary>
        public SetNetObjectAttribute(ushort archetypeId) => ArchetypeId = archetypeId;

        /// <summary>The archetype id.</summary>
        public ushort ArchetypeId { get; }
    }

    /// <summary>One reflected replicated field: how to reach it and its wire type.</summary>
    internal sealed class BoundField
    {
        public FieldInfo Info = null!;
        public FieldType Type;
        public bool IsEnum;
    }

    /// <summary>The cached reflection plan for one POCO type: its archetype id, schema, and field accessors.</summary>
    internal sealed class TypeBinding
    {
        public ushort ArchetypeId;
        public ReplicaSchema Schema = null!;
        public BoundField[] Fields = Array.Empty<BoundField>();
    }

    /// <summary>
    /// Attribute-driven schema + value binding. Call <see cref="Register{T}(ushort?)"/> once per POCO type on <b>both</b>
    /// ends (identical type/order), then use <c>world.SpawnBound</c> (server) and <c>view.Bind</c> /
    /// <c>client.BindVariables</c> (client) to move values between your objects and the replication layer automatically.
    /// </summary>
    public static class NetworkVariables
    {
        private static readonly Dictionary<Type, TypeBinding> Bindings = new Dictionary<Type, TypeBinding>();
        private static readonly object Gate = new object();

        /// <summary>
        /// Builds and registers the replica schema for <typeparamref name="T"/> from its <see cref="SetNetVariableAttribute"/>
        /// fields. Pass an archetype id, or omit it to read one from a <see cref="SetNetObjectAttribute"/> on the type.
        /// Idempotent per type.
        /// </summary>
        public static void Register<T>(ushort? archetypeId = null) => GetOrBuild(typeof(T), archetypeId);

        /// <summary>The archetype id registered for <typeparamref name="T"/>.</summary>
        public static ushort ArchetypeOf<T>() => For(typeof(T)).ArchetypeId;

        internal static TypeBinding For(Type t)
        {
            lock (Gate)
            {
                if (Bindings.TryGetValue(t, out var b)) return b;
            }
            return GetOrBuild(t, null);
        }

        private static TypeBinding GetOrBuild(Type t, ushort? archetypeId)
        {
            lock (Gate)
            {
                if (Bindings.TryGetValue(t, out var existing)) return existing;

                var id = archetypeId
                    ?? t.GetCustomAttribute<SetNetObjectAttribute>()?.ArchetypeId
                    ?? throw new InvalidOperationException(
                        $"No archetype id for {t.Name}. Pass one to Register<{t.Name}>(id) or add [SetNetObject(id)] to the type.");

                var members = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(f => (f, a: f.GetCustomAttribute<SetNetVariableAttribute>()))
                    .Where(x => x.a != null)
                    .OrderBy(x => x.a!.Order < 0 ? int.MaxValue : x.a!.Order)
                    .ThenBy(x => x.f.MetadataToken)
                    .ToArray();

                if (members.Length == 0)
                    throw new InvalidOperationException($"{t.Name} has no [SetNetVariable] fields.");

                var schema = ReplicaSchema.Create(id);
                var fields = new BoundField[members.Length];
                for (var i = 0; i < members.Length; i++)
                {
                    var (info, attr) = members[i];
                    var ft = MapType(info.FieldType, out var isEnum);
                    schema.Field(ft, attr!.Interpolate, attr.Precision);
                    fields[i] = new BoundField { Info = info, Type = ft, IsEnum = isEnum };
                }

                var binding = new TypeBinding { ArchetypeId = id, Schema = schema.Build(), Fields = fields };
                ReplicaRegistry.Register(binding.Schema);
                Bindings[t] = binding;
                return binding;
            }
        }

        private static FieldType MapType(Type t, out bool isEnum)
        {
            isEnum = false;
            if (t.IsEnum) { isEnum = true; return FieldType.Int; }
            if (t == typeof(bool)) return FieldType.Bool;
            if (t == typeof(byte)) return FieldType.Byte;
            if (t == typeof(int)) return FieldType.Int;
            if (t == typeof(uint)) return FieldType.UInt;
            if (t == typeof(long)) return FieldType.Long;
            if (t == typeof(float)) return FieldType.Float;
            if (t == typeof(double)) return FieldType.Double;
            if (t == typeof(string)) return FieldType.String;
            if (t == typeof(Vec2)) return FieldType.Vector2;
            if (t == typeof(Vec3)) return FieldType.Vector3;
            if (t == typeof(Quat)) return FieldType.Quaternion;
            throw new NotSupportedException(
                $"[SetNetVariable] does not support {t.Name}. Use bool/byte/int/uint/long/float/double/string/Vec2/Vec3/Quat or an enum.");
        }

        // Copy the POCO's fields into a server entity's replicated values.
        internal static void Push(TypeBinding b, object obj, NetworkEntity e)
        {
            for (var i = 0; i < b.Fields.Length; i++)
            {
                var f = b.Fields[i];
                var v = f.Info.GetValue(obj);
                switch (f.Type)
                {
                    case FieldType.Bool: e.SetBool(i, (bool)v!); break;
                    case FieldType.Byte: e.SetInt(i, (byte)v!); break;
                    case FieldType.Int: e.SetInt(i, f.IsEnum ? Convert.ToInt64(v) : (int)v!); break;
                    case FieldType.UInt: e.SetInt(i, (uint)v!); break;
                    case FieldType.Long: e.SetInt(i, (long)v!); break;
                    case FieldType.Float: e.SetFloat(i, (float)v!); break;
                    case FieldType.Double: e.SetFloat(i, (double)v!); break;
                    case FieldType.String: e.SetString(i, (string?)v ?? ""); break;
                    case FieldType.Vector2: e.SetVec2(i, (Vec2)v!); break;
                    case FieldType.Vector3: e.SetVec3(i, (Vec3)v!); break;
                    case FieldType.Quaternion: e.SetQuat(i, (Quat)v!); break;
                }
            }
        }

        // Copy a client view's (interpolated) values into the POCO's fields.
        internal static void Pull(TypeBinding b, NetworkEntityView view, object obj)
        {
            for (var i = 0; i < b.Fields.Length; i++)
            {
                var f = b.Fields[i];
                object value = f.Type switch
                {
                    FieldType.Bool => view.GetBool(i),
                    FieldType.Byte => (byte)view.GetInt(i),
                    FieldType.Int => f.IsEnum ? Enum.ToObject(f.Info.FieldType, view.GetInt(i)) : (int)view.GetInt(i),
                    FieldType.UInt => (uint)view.GetInt(i),
                    FieldType.Long => view.GetInt(i),
                    FieldType.Float => (float)view.GetFloat(i),
                    FieldType.Double => view.GetFloat(i),
                    FieldType.String => view.GetString(i),
                    FieldType.Vector2 => view.GetVec2(i),
                    FieldType.Vector3 => view.GetVec3(i),
                    FieldType.Quaternion => view.GetQuat(i),
                    _ => throw new NotSupportedException(),
                };
                f.Info.SetValue(obj, value);
            }
        }
    }

    /// <summary>A server entity bound to a POCO: mutate <see cref="Target"/>, then call <see cref="Push"/> so the change replicates.</summary>
    public sealed class BoundEntity<T> where T : class
    {
        private readonly TypeBinding _binding;

        /// <summary>Your object; mutate its <c>[SetNetVariable]</c> fields, then <see cref="Push"/>.</summary>
        public T Target { get; }

        /// <summary>The underlying replicated entity (pass to <c>world.Despawn</c> when done).</summary>
        public NetworkEntity Entity { get; }

        internal BoundEntity(T target, NetworkEntity entity, TypeBinding binding)
        {
            Target = target;
            Entity = entity;
            _binding = binding;
            Push();   // seed initial values
        }

        /// <summary>Copies the POCO's current field values into the entity so they're sampled on the next server tick.</summary>
        public void Push() => NetworkVariables.Push(_binding, Target!, Entity);
    }

    /// <summary>A client view bound to a POCO: call <see cref="Pull"/> each frame and read <see cref="Target"/>.</summary>
    public sealed class BoundView<T> where T : class, new()
    {
        private readonly TypeBinding _binding;
        private readonly NetworkEntityView _view;

        /// <summary>Your object, updated by <see cref="Pull"/> from the replicated view.</summary>
        public T Target { get; }

        /// <summary>The underlying client view.</summary>
        public NetworkEntityView View => _view;

        internal BoundView(NetworkEntityView view, TypeBinding binding)
        {
            _view = view;
            _binding = binding;
            Target = new T();
            Pull();
        }

        /// <summary>Copies the view's current (interpolated) values into the POCO. Call once per frame after <c>ClientReplication.Update()</c>.</summary>
        public void Pull() => NetworkVariables.Pull(_binding, _view, Target!);
    }

    /// <summary>
    /// Tracks every replicated entity of archetype <typeparamref name="T"/> on the client, materialising a POCO per entity
    /// and keeping them in sync. Subscribe to <see cref="Spawned"/>/<see cref="Despawned"/>, call <see cref="Pull"/> each
    /// frame, and read <see cref="Values"/>.
    /// </summary>
    public sealed class VariableSet<T> where T : class, new()
    {
        private readonly Dictionary<uint, BoundView<T>> _map = new Dictionary<uint, BoundView<T>>();
        private readonly ushort _archetype;

        /// <summary>Raised when an entity of this archetype appears (its bound POCO).</summary>
        public event Action<T>? Spawned;

        /// <summary>Raised when an entity of this archetype is removed (its bound POCO).</summary>
        public event Action<T>? Despawned;

        internal VariableSet(ClientReplication client)
        {
            _archetype = NetworkVariables.For(typeof(T)).ArchetypeId;
            client.EntitySpawned += OnSpawn;
            client.EntityDespawned += OnDespawn;
            foreach (var v in client.Entities) OnSpawn(v);   // catch entities that already exist
        }

        /// <summary>The bound POCOs currently alive.</summary>
        public IEnumerable<T> Values => _map.Values.Select(b => b.Target);

        /// <summary>Refreshes every bound POCO from its view. Call once per frame after <c>ClientReplication.Update()</c>.</summary>
        public void Pull() { foreach (var b in _map.Values) b.Pull(); }

        private void OnSpawn(NetworkEntityView view)
        {
            if (view.ArchetypeId != _archetype || _map.ContainsKey(view.NetId)) return;
            var bound = view.Bind<T>();
            _map[view.NetId] = bound;
            Spawned?.Invoke(bound.Target);
        }

        private void OnDespawn(NetworkEntityView view)
        {
            if (!_map.TryGetValue(view.NetId, out var bound)) return;
            _map.Remove(view.NetId);
            Despawned?.Invoke(bound.Target);
        }
    }

    /// <summary>Server/client extensions for attribute-bound network variables.</summary>
    public static class NetworkVariableBindingExtensions
    {
        /// <summary>Spawns a replicated entity for <paramref name="target"/>'s archetype and binds the POCO to it.</summary>
        public static BoundEntity<T> SpawnBound<T>(this ServerReplication world, T target, Guid owner = default) where T : class
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var binding = NetworkVariables.For(typeof(T));
            var entity = world.Spawn(binding.ArchetypeId, owner);
            return new BoundEntity<T>(target, entity, binding);
        }

        /// <summary>Binds a fresh POCO of type <typeparamref name="T"/> to this view (its archetype must match).</summary>
        public static BoundView<T> Bind<T>(this NetworkEntityView view) where T : class, new()
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            var binding = NetworkVariables.For(typeof(T));
            if (view.ArchetypeId != binding.ArchetypeId)
                throw new InvalidOperationException($"View archetype {view.ArchetypeId} does not match {typeof(T).Name} archetype {binding.ArchetypeId}.");
            return new BoundView<T>(view, binding);
        }

        /// <summary>Tracks all client entities of archetype <typeparamref name="T"/>, materialising and syncing a POCO each.</summary>
        public static VariableSet<T> BindVariables<T>(this ClientReplication client) where T : class, new()
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new VariableSet<T>(client);
        }
    }
}
