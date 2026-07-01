using System;

namespace SetNet.StateSync
{
    /// <summary>
    /// A server-authoritative replicated entity: a stable network id, an archetype (schema), an optional owner peer, and
    /// the current values of its replicated fields. The game mutates the fields through the typed setters each frame; the
    /// <see cref="ServerReplication"/> samples them on its tick and streams deltas to observers. Field indices match the
    /// order they were declared in the archetype's <see cref="ReplicaSchema"/>.
    /// </summary>
    public sealed class NetworkEntity
    {
        /// <summary>The stable, unique network id used to correlate this entity across client and server.</summary>
        public uint NetId { get; }

        /// <summary>The archetype id selecting this entity's schema (and, on the client, which prefab to spawn).</summary>
        public ushort ArchetypeId { get; }

        /// <summary>The owning peer, or <see cref="Guid.Empty"/> for a server-owned entity. Owners may receive input authority.</summary>
        public Guid Owner { get; internal set; }

        internal readonly ReplicaSchema Schema;
        internal readonly FieldValue[] Values;

        internal NetworkEntity(uint netId, ushort archetypeId, Guid owner, ReplicaSchema schema)
        {
            NetId = netId;
            ArchetypeId = archetypeId;
            Owner = owner;
            Schema = schema;
            Values = new FieldValue[schema.Fields.Count];
            for (var i = 0; i < Values.Length; i++) Values[i] = FieldValue.Default(schema.Fields[i].Type);
        }

        /// <summary>Sets a boolean field.</summary>
        public void SetBool(int index, bool value) => Values[index] = FieldValue.Number(value ? 1 : 0);
        /// <summary>Sets an integer field (covers Byte/Int/UInt/Long).</summary>
        public void SetInt(int index, long value) => Values[index] = FieldValue.Number(value);
        /// <summary>Sets a float/double field.</summary>
        public void SetFloat(int index, double value) => Values[index] = FieldValue.Number(value);
        /// <summary>Sets a 2D vector field.</summary>
        public void SetVec2(int index, Vec2 value) => Values[index] = FieldValue.Vector2(value);
        /// <summary>Sets a 3D vector field.</summary>
        public void SetVec3(int index, Vec3 value) => Values[index] = FieldValue.Vector3(value);
        /// <summary>Sets a quaternion field.</summary>
        public void SetQuat(int index, Quat value) => Values[index] = FieldValue.Quaternion(value);
        /// <summary>Sets a string field.</summary>
        public void SetString(int index, string value) => Values[index] = FieldValue.String(value);

        /// <summary>Reads a boolean field.</summary>
        public bool GetBool(int index) => Values[index].Num != 0;
        /// <summary>Reads an integer field.</summary>
        public long GetInt(int index) => (long)Values[index].Num;
        /// <summary>Reads a float/double field.</summary>
        public double GetFloat(int index) => Values[index].Num;
        /// <summary>Reads a 3D vector field.</summary>
        public Vec3 GetVec3(int index) => Values[index].V3;
        /// <summary>Reads a quaternion field.</summary>
        public Quat GetQuat(int index) => Values[index].Q;
        /// <summary>Reads a string field.</summary>
        public string GetString(int index) => Values[index].Str ?? "";

        /// <summary>Returns a copy of the current field values (used as a delta baseline).</summary>
        internal FieldValue[] Snapshot()
        {
            var copy = new FieldValue[Values.Length];
            Array.Copy(Values, copy, Values.Length);
            return copy;
        }
    }
}
