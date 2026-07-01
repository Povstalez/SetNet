using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SetNet.Matchmaking
{
    /// <summary>Client → server matchmaking command. Hand-framed as a byte[] so it rides over any serializer.</summary>
    internal readonly struct MatchCommand
    {
        public readonly int CorrelationId;
        public readonly MatchOp Op;
        public readonly string Queue;
        public readonly int Skill;

        public MatchCommand(int correlationId, MatchOp op, string queue, int skill)
        {
            CorrelationId = correlationId;
            Op = op;
            Queue = queue ?? "";
            Skill = skill;
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write((byte)Op);
                w.Write(Queue);
                w.Write(Skill);
            }
            return ms.ToArray();
        }

        public static MatchCommand Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var corr = r.ReadInt32();
            var op = (MatchOp)r.ReadByte();
            var queue = r.ReadString();
            var skill = r.ReadInt32();
            return new MatchCommand(corr, op, queue, skill);
        }
    }

    /// <summary>Server → client reply to a command (correlated). Confirms the enqueue/cancel and carries the player's own id.</summary>
    internal readonly struct MatchReply
    {
        public readonly int CorrelationId;
        public readonly bool Success;
        public readonly string OwnPlayerId;
        public readonly string Error;

        private MatchReply(int corr, bool success, string ownId, string error)
        {
            CorrelationId = corr;
            Success = success;
            OwnPlayerId = ownId ?? "";
            Error = error ?? "";
        }

        public static MatchReply Ok(int corr, string ownId) => new MatchReply(corr, true, ownId, "");
        public static MatchReply Fail(int corr, string error) => new MatchReply(corr, false, "", error);

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(CorrelationId);
                w.Write(Success);
                if (Success) w.Write(OwnPlayerId);
                else w.Write(Error);
            }
            return ms.ToArray();
        }

        public static MatchReply Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var corr = r.ReadInt32();
            var success = r.ReadBoolean();
            return success ? Ok(corr, r.ReadString()) : Fail(corr, r.ReadString());
        }
    }

    /// <summary>Server → client push event: a match was found. Sent only to the players in the match, tagged with the recipient's id.</summary>
    internal readonly struct MatchEvent
    {
        public readonly string Recipient;
        public readonly string Queue;
        public readonly string RoomCode;
        public readonly IReadOnlyList<string> Players;

        public MatchEvent(string recipient, string queue, string roomCode, IReadOnlyList<string> players)
        {
            Recipient = recipient ?? "";
            Queue = queue ?? "";
            RoomCode = roomCode ?? "";
            Players = players ?? Array.Empty<string>();
        }

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Recipient);
                w.Write(Queue);
                w.Write(RoomCode);
                w.Write(Players.Count);
                foreach (var p in Players) w.Write(p);
            }
            return ms.ToArray();
        }

        public static MatchEvent Decode(byte[] frame)
        {
            using var ms = new MemoryStream(frame);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var recipient = r.ReadString();
            var queue = r.ReadString();
            var roomCode = r.ReadString();
            var count = r.ReadInt32();
            var players = new List<string>(count);
            for (var i = 0; i < count; i++) players.Add(r.ReadString());
            return new MatchEvent(recipient, queue, roomCode, players);
        }
    }
}
