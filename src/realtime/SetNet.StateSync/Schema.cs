using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SetNet.StateSync
{
    /// <summary>Describes one replicated field: its type, whether the client interpolates it, and optional float quantization.</summary>
    public sealed class FieldDef
    {
        /// <summary>The field's wire/interpolation type.</summary>
        public FieldType Type { get; }

        /// <summary>Whether the client smoothly interpolates this field between snapshots (float/double/vector/quaternion only).</summary>
        public bool Interpolate { get; }

        /// <summary>
        /// Quantization step for float/vector fields (e.g. 0.001 = millimetre precision). When &gt; 0 the value is sent as a
        /// scaled integer instead of a raw float, shrinking the packet. 0 = send raw floats. Ignored for non-float types.
        /// </summary>
        public float Precision { get; }

        /// <summary>Creates a field definition.</summary>
        public FieldDef(FieldType type, bool interpolate = false, float precision = 0f)
        {
            Type = type;
            Interpolate = interpolate;
            Precision = precision < 0 ? 0 : precision;
        }
    }

    /// <summary>
    /// The immutable shape of one entity archetype: an id shared by client and server, and an ordered list of replicated
    /// fields. Both ends must register identical schemas (same id → same fields, same order) so field indices line up.
    /// </summary>
    public sealed class ReplicaSchema
    {
        /// <summary>The archetype id, matched across client and server.</summary>
        public ushort ArchetypeId { get; }

        /// <summary>The ordered replicated fields; a field's index is its position here.</summary>
        public IReadOnlyList<FieldDef> Fields { get; }

        private ReplicaSchema(ushort archetypeId, IReadOnlyList<FieldDef> fields)
        {
            ArchetypeId = archetypeId;
            Fields = fields;
        }

        /// <summary>Starts building a schema for the given archetype id.</summary>
        public static Builder Create(ushort archetypeId) => new Builder(archetypeId);

        /// <summary>Fluent schema builder — add fields in order, then <see cref="Build"/>.</summary>
        public sealed class Builder
        {
            private readonly ushort _archetypeId;
            private readonly List<FieldDef> _fields = new List<FieldDef>();

            internal Builder(ushort archetypeId) => _archetypeId = archetypeId;

            /// <summary>Appends a field.</summary>
            public Builder Field(FieldType type, bool interpolate = false, float precision = 0f)
            {
                _fields.Add(new FieldDef(type, interpolate, precision));
                return this;
            }

            /// <summary>Builds the immutable schema.</summary>
            public ReplicaSchema Build() => new ReplicaSchema(_archetypeId, _fields.ToArray());
        }
    }

    /// <summary>
    /// The process-wide registry of archetype schemas. Register every archetype at startup on both the client and the
    /// server (identical definitions). Thread-safe.
    /// </summary>
    public static class ReplicaRegistry
    {
        private static readonly ConcurrentDictionary<ushort, ReplicaSchema> Schemas
            = new ConcurrentDictionary<ushort, ReplicaSchema>();

        /// <summary>Registers (or replaces) a schema by its archetype id.</summary>
        public static void Register(ReplicaSchema schema)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            Schemas[schema.ArchetypeId] = schema;
        }

        /// <summary>Gets a registered schema by archetype id, or throws if it hasn't been registered on this side.</summary>
        public static ReplicaSchema Get(ushort archetypeId)
            => Schemas.TryGetValue(archetypeId, out var s)
                ? s
                : throw new InvalidOperationException($"No replica schema registered for archetype {archetypeId}. Register it (identically) on both client and server.");

        /// <summary>Tries to get a registered schema.</summary>
        public static bool TryGet(ushort archetypeId, out ReplicaSchema schema) => Schemas.TryGetValue(archetypeId, out schema!);
    }
}
