using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SetNet.Rooms
{
    /// <summary>Command operations (client → server) within the Rooms protocol channel.</summary>
    internal enum RoomOp : ushort
    {
        /// <summary>Create and join a new room.</summary>
        Create = 1,
        /// <summary>Join an existing room by code.</summary>
        Join = 2,
        /// <summary>Leave the current room.</summary>
        Leave = 3,
        /// <summary>Broadcast a payload to the other room members.</summary>
        Broadcast = 4,
    }

    /// <summary>Push events (server → client) within the Rooms protocol channel.</summary>
    internal enum RoomEvt : ushort
    {
        /// <summary>Another player joined the room.</summary>
        PlayerJoined = 10,
        /// <summary>A player left the room.</summary>
        PlayerLeft = 11,
        /// <summary>A broadcast message from another member.</summary>
        Message = 12,
        /// <summary>The room closed.</summary>
        Closed = 13,
    }

    /// <summary>
    /// Body codecs for the Rooms channel. The unified protocol envelope already carries kind/channel/op/correlation,
    /// so these encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic.
    /// </summary>
    internal static class RoomWire
    {
        /// <summary>Create-command body: the requested max players.</summary>
        public static byte[] EncodeCreate(int maxPlayers)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(maxPlayers);
            return ms.ToArray();
        }

        /// <summary>Reads a create-command body.</summary>
        public static int DecodeCreate(byte[] body)
        {
            if (body == null || body.Length < 4) return 0;
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadInt32();
        }

        /// <summary>Join-command body: the room code.</summary>
        public static byte[] EncodeJoin(string code)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(code ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a join-command body.</summary>
        public static string DecodeJoin(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }

        /// <summary>Create/Join reply body: the room code, the caller's player id, and the member list.</summary>
        public static byte[] EncodeReply(string code, string ownId, IReadOnlyList<string> members)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(code ?? "");
                w.Write(ownId ?? "");
                w.Write(members?.Count ?? 0);
                if (members != null) foreach (var m in members) w.Write(m ?? "");
            }
            return ms.ToArray();
        }

        /// <summary>Reads a Create/Join reply body.</summary>
        public static (string code, string ownId, List<string> members) DecodeReply(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var code = r.ReadString();
            var ownId = r.ReadString();
            var count = r.ReadInt32();
            var members = new List<string>(count);
            for (var i = 0; i < count; i++) members.Add(r.ReadString());
            return (code, ownId, members);
        }

        /// <summary>Frames a broadcast payload as [ushort messageType][payload].</summary>
        public static byte[] FrameBroadcast(ushort messageType, byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(messageType);
                w.Write(payload ?? Array.Empty<byte>());
            }
            return ms.ToArray();
        }

        /// <summary>Unframes a broadcast payload.</summary>
        public static (ushort messageType, byte[] payload) UnframeBroadcast(byte[] body)
        {
            if (body == null || body.Length < 2) return (0, Array.Empty<byte>());
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var type = r.ReadUInt16();
            var payload = r.ReadBytes(body.Length - 2);
            return (type, payload);
        }

        /// <summary>Player-joined/left event body: [room code][player id].</summary>
        public static byte[] EncodePlayer(string code, string playerId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(code ?? "");
                w.Write(playerId ?? "");
            }
            return ms.ToArray();
        }

        /// <summary>Reads a player-joined/left event body.</summary>
        public static (string code, string playerId) DecodePlayer(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }

        /// <summary>Message event body: [room code][sender player id][ushort messageType][payload].</summary>
        public static byte[] EncodeMessage(string code, string sender, ushort messageType, byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(code ?? "");
                w.Write(sender ?? "");
                w.Write(messageType);
                w.Write(payload?.Length ?? 0);
                if (payload != null) w.Write(payload);
            }
            return ms.ToArray();
        }

        /// <summary>Reads a message event body.</summary>
        public static (string code, string sender, ushort messageType, byte[] payload) DecodeMessage(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var code = r.ReadString();
            var sender = r.ReadString();
            var type = r.ReadUInt16();
            var len = r.ReadInt32();
            var payload = r.ReadBytes(len);
            return (code, sender, type, payload);
        }

        /// <summary>Room-closed event body: [room code].</summary>
        public static byte[] EncodeCode(string code)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(code ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads a room-closed event body.</summary>
        public static string DecodeCode(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }
    }
}
