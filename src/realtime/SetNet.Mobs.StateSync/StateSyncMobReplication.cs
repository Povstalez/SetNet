using System;
using System.Collections.Concurrent;
using SetNet.Mobs;
using SetNet.StateSync;
using SsVec3 = SetNet.StateSync.Vec3;
using GeoVec3 = SetNet.GeoData.Vec3;

namespace SetNet.Mobs.StateSync
{
    /// <summary>
    /// A <see cref="IMobReplication"/> that mirrors mobs into a <see cref="ServerReplication"/> world: each mob becomes
    /// a <see cref="NetworkEntity"/> of a registered archetype, so nearby players observe mob position/health/target/cast
    /// through the same delta-compressed StateSync snapshot stream as everything else — no separate mob replication
    /// wire. Position is interpolated; discrete fields (health, target hash, cast) snap. Register once and hand it to
    /// <see cref="MobOptions.Replication"/>; the extension methods make wiring one line.
    /// </summary>
    public sealed class StateSyncMobReplication : IMobReplication
    {
        /// <summary>Default archetype id used for the mob schema when the caller doesn't specify one.</summary>
        public const ushort DefaultArchetypeId = 4100;

        // Field indices in the schema (declared in this order below).
        private const int FieldPosition = 0;   // Vector3, interpolated
        private const int FieldVelocity = 1;   // Vector3
        private const int FieldHealth = 2;     // Float
        private const int FieldMaxHealth = 3;  // Float
        private const int FieldTargetHash = 4; // Int (hash of target key; 0 = none)
        private const int FieldCastHash = 5;   // Int (hash of casting ability id; 0 = none)
        private const int FieldCastRemaining = 6; // Int (ms remaining on the cast)

        private readonly ServerReplication _world;
        private readonly ushort _archetypeId;
        private readonly ConcurrentDictionary<string, NetworkEntity> _entities = new ConcurrentDictionary<string, NetworkEntity>();

        /// <summary>
        /// Creates the adapter over a StateSync world and registers the mob archetype schema (idempotent). Pass the
        /// same <paramref name="archetypeId"/> when registering the identical schema on the client side.
        /// </summary>
        public StateSyncMobReplication(ServerReplication world, ushort archetypeId = DefaultArchetypeId)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _archetypeId = archetypeId;
            ReplicaRegistry.Register(BuildSchema(archetypeId));
        }

        /// <summary>The archetype id used for mob entities — register the same schema (via <see cref="BuildSchema"/>) on the client.</summary>
        public ushort ArchetypeId => _archetypeId;

        /// <summary>Builds the mob archetype schema. Call with the same id on the client so field indices line up.</summary>
        public static ReplicaSchema BuildSchema(ushort archetypeId = DefaultArchetypeId)
            => ReplicaSchema.Create(archetypeId)
                .Field(FieldType.Vector3, interpolate: true, precision: 0.01f)   // position
                .Field(FieldType.Vector3)                                        // velocity
                .Field(FieldType.Float, interpolate: true)                       // health
                .Field(FieldType.Float)                                          // max health
                .Field(FieldType.Int)                                            // target hash
                .Field(FieldType.Int)                                            // cast ability hash
                .Field(FieldType.Int)                                            // cast remaining ms
                .Build();

        /// <inheritdoc/>
        public void OnMobSpawned(MobInstance mob)
        {
            if (mob == null) return;
            var entity = _world.Spawn(_archetypeId);
            Apply(entity, mob);
            _entities[mob.Id] = entity;
        }

        /// <inheritdoc/>
        public void OnMobUpdated(MobInstance mob)
        {
            if (mob != null && _entities.TryGetValue(mob.Id, out var entity)) Apply(entity, mob);
        }

        /// <inheritdoc/>
        public void OnMobDespawned(string mobId)
        {
            if (mobId != null && _entities.TryRemove(mobId, out var entity)) _world.Despawn(entity);
        }

        private static void Apply(NetworkEntity e, MobInstance mob)
        {
            e.SetVec3(FieldPosition, ToSs(mob.Position));
            e.SetVec3(FieldVelocity, ToSs(mob.Velocity));
            e.SetFloat(FieldHealth, mob.Health);
            e.SetFloat(FieldMaxHealth, mob.MaxHealth);
            e.SetInt(FieldTargetHash, StableHash(mob.Target));
            e.SetInt(FieldCastHash, StableHash(mob.Casting?.AbilityId));
            e.SetInt(FieldCastRemaining, mob.Casting?.RemainingMs ?? 0);
        }

        /// <summary>Converts a GeoData vector (the mob position type) to a StateSync vector.</summary>
        private static SsVec3 ToSs(GeoVec3 v) => new SsVec3(v.X, v.Y, v.Z);

        // A stable, framework-version-independent hash so the client can map a hash back to a known target/ability id.
        private static int StableHash(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                var hash = 23;
                foreach (var c in s) hash = hash * 31 + c;
                return hash;
            }
        }
    }

    /// <summary>One-line wiring for the StateSync mob replication adapter.</summary>
    public static class StateSyncMobReplicationExtensions
    {
        /// <summary>
        /// Creates a StateSync-backed <see cref="IMobReplication"/> for a world — pass it to
        /// <c>MobOptions.Replication</c> (e.g. <c>server.UseMobs(new MobOptions { Replication = world.StateSyncReplication() })</c>).
        /// Mobs replicate through this world's observers, so nearby players must be observers of it.
        /// </summary>
        public static IMobReplication StateSyncReplication(this ServerReplication world, ushort archetypeId = StateSyncMobReplication.DefaultArchetypeId)
            => new StateSyncMobReplication(world, archetypeId);
    }
}
