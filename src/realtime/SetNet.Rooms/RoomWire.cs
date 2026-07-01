using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SetNet.Rooms
{
    /// <summary>Client → server command. Hand-framed as a byte[] so it rides over any serializer.</summary>
    internal readonly struct RoomCommand
    {
        public readonly int CorrelationId;
        public readonly RoomOp Op;
        public readonly string Code;
        public readonly int MaxPlayers;
        public readonly byte[] Payload;

        public RoomCommand(int correlationId, RoomOp op, string code, int maxPlayers, byte[] payload)
        {
            CorrelationId = correlationId;
            Op = op;
            Code = code ?? "";
            MaxPlayers = maxPlayers;
            Payload = payload ?? Array.Empty<byte>();
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write((byte)Op);
                w.Write(Code);
                w.Write(MaxPlayers);
                w.Write(Payload.Length);
                w.Write(Payload);
            }
            return ms.ToArray();
        }

        public static RoomCommand Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var corr = r.ReadInt32();
            var op = (RoomOp)r.ReadByte();
            var code = r.ReadString();
            var max = r.ReadInt32();
            var len = r.ReadInt32();
            var payload = r.ReadBytes(len);
            return new RoomCommand(corr, op, code, max, payload);
        }
    }

    /// <summary>Server → client reply to a command (correlated).</summary>
    internal readonly struct RoomReply
    {
        public readonly int CorrelationId;
        public readonly bool Success;
        public readonly string Code;
        public readonly string OwnPlayerId;
        public readonly IReadOnlyList<string> Members;
        public readonly string Error;

        private RoomReply(int corr, bool success, string code, string ownId, IReadOnlyList<string> members, string error)
        {
            CorrelationId = corr;
            Success = success;
            Code = code ?? "";
            OwnPlayerId = ownId ?? "";
            Members = members ?? Array.Empty<string>();
            Error = error ?? "";
        }

        public static RoomReply Ok(int corr, string code, string ownId, IReadOnlyList<string> members)
            => new RoomReply(corr, true, code, ownId, members, "");

        public static RoomReply Fail(int corr, string error)
            => new RoomReply(corr, false, "", "", Array.Empty<string>(), error);

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write(Success);
                if (Success)
                {
                    w.Write(Code);
                    w.Write(OwnPlayerId);
                    w.Write(Members.Count);
                    foreach (var m in Members) w.Write(m);
                }
                else
                {
                    w.Write(Error);
                }
            }
            return ms.ToArray();
        }

        public static RoomReply Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var corr = r.ReadInt32();
            var success = r.ReadBoolean();
            if (!success) return Fail(corr, r.ReadString());
            var code = r.ReadString();
            var ownId = r.ReadString();
            var count = r.ReadInt32();
            var members = new List<string>(count);
            for (var i = 0; i < count; i++) members.Add(r.ReadString());
            return Ok(corr, code, ownId, members);
        }
    }

    /// <summary>Server → client push event within a room.</summary>
    internal readonly struct RoomEvent
    {
        public readonly string Code;
        public readonly RoomEventType Type;
        public readonly string PlayerId;
        public readonly byte[] Payload;

        public RoomEvent(string code, RoomEventType type, string playerId, byte[] payload)
        {
            Code = code ?? "";
            Type = type;
            PlayerId = playerId ?? "";
            Payload = payload ?? Array.Empty<byte>();
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Code);
                w.Write((byte)Type);
                w.Write(PlayerId);
                w.Write(Payload.Length);
                w.Write(Payload);
            }
            return ms.ToArray();
        }

        public static RoomEvent Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var code = r.ReadString();
            var type = (RoomEventType)r.ReadByte();
            var playerId = r.ReadString();
            var len = r.ReadInt32();
            var payload = r.ReadBytes(len);
            return new RoomEvent(code, type, playerId, payload);
        }
    }
}
