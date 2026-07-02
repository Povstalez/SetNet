using System.Collections.Generic;
using System.IO;
using System.Text;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>Command operations (client → server) within the Mobs protocol channel.</summary>
    internal enum MobsOp : ushort
    {
        /// <summary>A player attacks a mob with an ability (validated server-side; replies ok/reject).</summary>
        Attack = 1,
    }

    /// <summary>Push events (server → client) within the Mobs protocol channel.</summary>
    internal enum MobsEvt : ushort
    {
        /// <summary>A mob became visible to the client (interest-filtered).</summary>
        MobSpawned = 10,
        /// <summary>A mob left the client's view / was destroyed (interest-filtered).</summary>
        MobDespawned = 11,
        /// <summary>A mob ability resolved against targets (telegraph/VFX cue the raw state doesn't convey).</summary>
        MobAttack = 12,
        /// <summary>A mob acquired a target.</summary>
        MobAggro = 13,
        /// <summary>A mob died.</summary>
        MobDeath = 14,
    }

    /// <summary>
    /// Body codecs for the Mobs channel. The unified protocol envelope carries kind/channel/op/correlation, so these
    /// encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic (mirrors RoomWire).
    /// </summary>
    internal static class MobsWire
    {
        private static void WriteVec3(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static Vec3 ReadVec3(BinaryReader r) => new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ---- Attack command / reply ----

        /// <summary>Attack-command body: [mobId][abilityId].</summary>
        public static byte[] EncodeAttack(string mobId, string abilityId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(mobId ?? ""); w.Write(abilityId ?? ""); }
            return ms.ToArray();
        }

        /// <summary>Reads an attack-command body.</summary>
        public static (string mobId, string abilityId) DecodeAttack(byte[] body)
        {
            if (body == null || body.Length == 0) return ("", "");
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }

        /// <summary>Attack-reply body: [bool accepted][string reason].</summary>
        public static byte[] EncodeAttackReply(bool accepted, string reason)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(accepted); w.Write(reason ?? ""); }
            return ms.ToArray();
        }

        /// <summary>Reads an attack-reply body.</summary>
        public static (bool accepted, string reason) DecodeAttackReply(byte[] body)
        {
            if (body == null || body.Length == 0) return (false, "");
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadBoolean(), r.ReadString());
        }

        // ---- spawn / despawn events ----

        /// <summary>Spawn-event body: full mob snapshot enough for a client to represent it.</summary>
        public static byte[] EncodeSpawn(MobInstance m)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(m.Id ?? "");
                w.Write(m.Type ?? "");
                WriteVec3(w, m.Position);
                w.Write(m.Health);
                w.Write(m.MaxHealth);
                w.Write(m.Zone ?? "");
                w.Write(m.Faction ?? "");
            }
            return ms.ToArray();
        }

        /// <summary>Reads a spawn-event body into a <see cref="MobSpawnedInfo"/>.</summary>
        public static MobSpawnedInfo DecodeSpawn(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return new MobSpawnedInfo(r.ReadString(), r.ReadString(), ReadVec3(r), r.ReadDouble(), r.ReadDouble(), r.ReadString(), r.ReadString());
        }

        /// <summary>Despawn-event body: [mobId].</summary>
        public static byte[] EncodeId(string mobId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(mobId ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a single-id body (despawn).</summary>
        public static string DecodeId(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }

        // ---- attack event ----

        /// <summary>Attack-event body: [mobId][abilityId][damage][targets...].</summary>
        public static byte[] EncodeAttackEvent(string mobId, string abilityId, double damage, IReadOnlyList<string> targets)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(mobId ?? "");
                w.Write(abilityId ?? "");
                w.Write(damage);
                w.Write(targets?.Count ?? 0);
                if (targets != null) foreach (var t in targets) w.Write(t ?? "");
            }
            return ms.ToArray();
        }

        /// <summary>Reads an attack-event body.</summary>
        public static MobAttackInfo DecodeAttackEvent(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var mobId = r.ReadString();
            var abilityId = r.ReadString();
            var damage = r.ReadDouble();
            var count = r.ReadInt32();
            var targets = new List<string>(count);
            for (var i = 0; i < count; i++) targets.Add(r.ReadString());
            return new MobAttackInfo(mobId, abilityId, damage, targets);
        }

        // ---- aggro event ----

        /// <summary>Aggro-event body: [mobId][targetKey].</summary>
        public static byte[] EncodeAggro(string mobId, string targetKey)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) { w.Write(mobId ?? ""); w.Write(targetKey ?? ""); }
            return ms.ToArray();
        }

        /// <summary>Reads an aggro-event body.</summary>
        public static MobAggroInfo DecodeAggro(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return new MobAggroInfo(r.ReadString(), r.ReadString());
        }

        // ---- death event ----

        /// <summary>Death-event body: [mobId][killerKey][position].</summary>
        public static byte[] EncodeDeath(string mobId, string? killerKey, Vec3 position)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(mobId ?? "");
                w.Write(killerKey ?? "");
                WriteVec3(w, position);
            }
            return ms.ToArray();
        }

        /// <summary>Reads a death-event body.</summary>
        public static MobDeathInfo DecodeDeath(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var mobId = r.ReadString();
            var killer = r.ReadString();
            var pos = ReadVec3(r);
            return new MobDeathInfo(mobId, string.IsNullOrEmpty(killer) ? null : killer, pos);
        }
    }
}
