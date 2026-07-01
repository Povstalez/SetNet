using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SetNet.StateSync
{
    /// <summary>
    /// Hand-framed encode/decode for the state-sync protocol (serializer-agnostic <c>byte[]</c>, like the other SetNet
    /// companion packages). Snapshot entries are delta-compressed: a full entry carries the archetype and every field;
    /// a delta entry carries only the fields that changed since the client's acknowledged baseline, selected by a bitmask.
    /// </summary>
    internal static class Wire
    {
        // ---- Spawn (reliable) : netId, archetype, isOwner, full field values ----
        public static byte[] EncodeSpawn(uint netId, ushort archetype, bool isOwner, ReplicaSchema schema, FieldValue[] values)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(netId);
                w.Write(archetype);
                w.Write(isOwner);
                WriteAllFields(w, schema, values);
            }
            return ms.ToArray();
        }

        public static (uint netId, ushort archetype, bool isOwner, FieldValue[] values) DecodeSpawn(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var netId = r.ReadUInt32();
            var archetype = r.ReadUInt16();
            var isOwner = r.ReadBoolean();
            var schema = ReplicaRegistry.Get(archetype);
            var values = ReadAllFields(r, schema);
            return (netId, archetype, isOwner, values);
        }

        // ---- Despawn (reliable) : netId ----
        public static byte[] EncodeDespawn(uint netId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(netId);
            return ms.ToArray();
        }

        public static uint DecodeDespawn(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadUInt32();
        }

        // ---- Ack (unreliable) : tick ----
        public static byte[] EncodeAck(int tick)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(tick);
            return ms.ToArray();
        }

        public static int DecodeAck(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadInt32();
        }

        // ---- Input (unreliable) : seq, payload ----
        public static byte[] EncodeInput(uint seq, byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(seq);
                w.Write(payload.Length);
                w.Write(payload);
            }
            return ms.ToArray();
        }

        public static (uint seq, byte[] payload) DecodeInput(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var seq = r.ReadUInt32();
            var len = r.ReadInt32();
            return (seq, r.ReadBytes(len));
        }

        // ---- Field encoding helpers (shared by spawn + snapshot) ----

        public static void WriteAllFields(BinaryWriter w, ReplicaSchema schema, FieldValue[] values)
        {
            for (var i = 0; i < schema.Fields.Count; i++)
                values[i].Write(w, schema.Fields[i]);
        }

        public static FieldValue[] ReadAllFields(BinaryReader r, ReplicaSchema schema)
        {
            var values = new FieldValue[schema.Fields.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = FieldValue.Read(r, schema.Fields[i]);
            return values;
        }

        /// <summary>Writes only the fields that differ from <paramref name="baseline"/>, preceded by a presence bitmask. Returns whether anything changed.</summary>
        public static bool WriteMaskedFields(BinaryWriter w, ReplicaSchema schema, FieldValue[] baseline, FieldValue[] current)
        {
            var n = schema.Fields.Count;
            var mask = new byte[(n + 7) / 8];
            var any = false;
            for (var i = 0; i < n; i++)
            {
                if (!current[i].Equals(baseline[i]))
                {
                    mask[i >> 3] |= (byte)(1 << (i & 7));
                    any = true;
                }
            }
            w.Write(mask);
            for (var i = 0; i < n; i++)
                if ((mask[i >> 3] & (1 << (i & 7))) != 0)
                    current[i].Write(w, schema.Fields[i]);
            return any;
        }

        /// <summary>Reads a masked field set, applying changed fields onto a copy of <paramref name="baseline"/>.</summary>
        public static FieldValue[] ReadMaskedFields(BinaryReader r, ReplicaSchema schema, FieldValue[] baseline)
        {
            var n = schema.Fields.Count;
            var result = new FieldValue[n];
            Array.Copy(baseline, result, Math.Min(baseline.Length, n));
            var mask = r.ReadBytes((n + 7) / 8);
            for (var i = 0; i < n; i++)
                if ((mask[i >> 3] & (1 << (i & 7))) != 0)
                    result[i] = FieldValue.Read(r, schema.Fields[i]);
            return result;
        }
    }
}
