using System;
using System.IO;
using System.Text;
using SetNet.GeoData;

namespace SetNet.NPC
{
    /// <summary>Command operations (client → server) within the NPC protocol channel.</summary>
    internal enum NpcOp : ushort
    {
        /// <summary>Interact with an instance (correlated request → <see cref="NpcResponse"/>).</summary>
        Interact = 1,
        /// <summary>Subscribe to a zone's spawn/despawn stream (correlated request → the current instance list).</summary>
        EnterZone = 2,
        /// <summary>Unsubscribe from a zone (fire-and-forget).</summary>
        LeaveZone = 3,
    }

    /// <summary>Push events (server → client) within the NPC protocol channel.</summary>
    internal enum NpcEvt : ushort
    {
        /// <summary>An instance the player is interested in spawned.</summary>
        Spawned = 10,
        /// <summary>An instance the player is interested in despawned.</summary>
        Despawned = 11,
    }

    /// <summary>
    /// Body codecs for the NPC channel. The unified protocol envelope already carries kind/channel/op/correlation, so
    /// these encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic. Opaque
    /// <c>Metadata</c>/<c>Payload</c> ride through as length-prefixed raw bytes.
    /// </summary>
    internal static class NpcWire
    {
        // ---- Interact command: [npcId][action][payload] ----

        public static byte[] EncodeInteract(string npcId, string action, byte[]? payload)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(npcId ?? "");
                w.Write(action ?? "");
                WriteBytes(w, payload);
            }
            return ms.ToArray();
        }

        public static (string npcId, string action, byte[] payload) DecodeInteract(byte[] body)
        {
            using var ms = new MemoryStream(body ?? Array.Empty<byte>());
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var npcId = r.ReadString();
            var action = r.ReadString();
            var payload = ReadBytes(r);
            return (npcId, action, payload);
        }

        // ---- Interact reply: [ok][message][payload][hasCapability][capability] ----

        public static byte[] EncodeResponse(NpcResponse resp)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(resp.Ok);
                w.Write(resp.Message ?? "");
                WriteBytes(w, resp.Payload);
                var hasCap = resp.Capability != null;
                w.Write(hasCap);
                if (hasCap) w.Write(resp.Capability!);
            }
            return ms.ToArray();
        }

        public static NpcResponse DecodeResponse(byte[] body)
        {
            using var ms = new MemoryStream(body ?? Array.Empty<byte>());
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var ok = r.ReadBoolean();
            var message = r.ReadString();
            var payload = ReadBytes(r);
            var hasCap = r.ReadBoolean();
            var capability = hasCap ? r.ReadString() : null;
            return new NpcResponse(ok, message, payload, capability);
        }

        // ---- Zone command: [zone] ----

        public static byte[] EncodeZone(string zone)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(zone ?? "");
            return ms.ToArray();
        }

        public static string DecodeZone(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }

        // ---- Instance (in a Spawned event, or an element of the EnterZone reply list) ----
        //      [id][type][zone][x][y][z][metadata] ----

        private static void WriteInstance(BinaryWriter w, NpcInstance inst)
        {
            w.Write(inst.Id ?? "");
            w.Write(inst.Type ?? "");
            w.Write(inst.Zone ?? "");
            w.Write(inst.Position.X);
            w.Write(inst.Position.Y);
            w.Write(inst.Position.Z);
            WriteBytes(w, inst.Metadata);
        }

        private static NpcInstance ReadInstance(BinaryReader r)
        {
            var id = r.ReadString();
            var type = r.ReadString();
            var zone = r.ReadString();
            var x = r.ReadSingle();
            var y = r.ReadSingle();
            var z = r.ReadSingle();
            var metadata = ReadBytes(r);
            return new NpcInstance(id, type, new Vec3(x, y, z), zone, metadata);
        }

        /// <summary>Encodes a single instance (used by the Spawned event body).</summary>
        public static byte[] EncodeInstance(NpcInstance inst)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) WriteInstance(w, inst);
            return ms.ToArray();
        }

        /// <summary>Decodes a single instance (used by the Spawned event body).</summary>
        public static NpcInstance DecodeInstance(byte[] body)
        {
            using var ms = new MemoryStream(body ?? Array.Empty<byte>());
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return ReadInstance(r);
        }

        /// <summary>Encodes the EnterZone reply: the current interest-visible instances in the zone.</summary>
        public static byte[] EncodeInstanceList(System.Collections.Generic.IReadOnlyList<NpcInstance> instances)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(instances?.Count ?? 0);
                if (instances != null) foreach (var inst in instances) WriteInstance(w, inst);
            }
            return ms.ToArray();
        }

        /// <summary>Decodes the EnterZone reply instance list.</summary>
        public static System.Collections.Generic.List<NpcInstance> DecodeInstanceList(byte[] body)
        {
            var list = new System.Collections.Generic.List<NpcInstance>();
            if (body == null || body.Length == 0) return list;
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++) list.Add(ReadInstance(r));
            return list;
        }

        // ---- Despawned event: [id][zone] (zone lets the client filter co-located streams) ----

        public static byte[] EncodeDespawned(string id, string zone)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(id ?? "");
                w.Write(zone ?? "");
            }
            return ms.ToArray();
        }

        public static (string id, string zone) DecodeDespawned(byte[] body)
        {
            using var ms = new MemoryStream(body ?? Array.Empty<byte>());
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }

        // ---- length-prefixed opaque bytes ----

        private static void WriteBytes(BinaryWriter w, byte[]? data)
        {
            var bytes = data ?? Array.Empty<byte>();
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        private static byte[] ReadBytes(BinaryReader r)
        {
            var len = r.ReadInt32();
            return len <= 0 ? Array.Empty<byte>() : r.ReadBytes(len);
        }
    }
}
