using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SetNet.Matchmaking
{
    /// <summary>Command operations (client → server) within the Matchmaking protocol channel.</summary>
    internal enum MatchOp : ushort
    {
        /// <summary>Enter a queue.</summary>
        Enqueue = 1,
        /// <summary>Leave the queue.</summary>
        Cancel = 2,
    }

    /// <summary>Push events (server → client) within the Matchmaking protocol channel.</summary>
    internal enum MatchEvt : ushort
    {
        /// <summary>A match was formed for this player.</summary>
        MatchFound = 10,
    }

    /// <summary>
    /// Body codecs for the Matchmaking channel. The unified envelope carries kind/channel/op/correlation, so these
    /// encode only the payload fields — hand-framed as <c>byte[]</c> to stay serializer-agnostic.
    /// </summary>
    internal static class MatchWire
    {
        /// <summary>Enqueue-command body: [queue][skill].</summary>
        public static byte[] EncodeEnqueue(string queue, int skill)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(queue ?? "");
                w.Write(skill);
            }
            return ms.ToArray();
        }

        /// <summary>Reads an enqueue-command body.</summary>
        public static (string queue, int skill) DecodeEnqueue(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return (r.ReadString(), r.ReadInt32());
        }

        /// <summary>Enqueue/Cancel reply body: the caller's player id.</summary>
        public static byte[] EncodeReply(string ownId)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) w.Write(ownId ?? "");
            return ms.ToArray();
        }

        /// <summary>Reads an enqueue/cancel reply body.</summary>
        public static string DecodeReply(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            return r.ReadString();
        }

        /// <summary>Match-found event body: [recipient][queue][roomCode][players…].</summary>
        public static byte[] EncodeMatch(string recipient, string queue, string roomCode, IReadOnlyList<string> players)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(recipient ?? "");
                w.Write(queue ?? "");
                w.Write(roomCode ?? "");
                w.Write(players?.Count ?? 0);
                if (players != null) foreach (var p in players) w.Write(p ?? "");
            }
            return ms.ToArray();
        }

        /// <summary>Reads a match-found event body.</summary>
        public static (string recipient, string queue, string roomCode, List<string> players) DecodeMatch(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var recipient = r.ReadString();
            var queue = r.ReadString();
            var roomCode = r.ReadString();
            var count = r.ReadInt32();
            var players = new List<string>(count);
            for (var i = 0; i < count; i++) players.Add(r.ReadString());
            return (recipient, queue, roomCode, players);
        }
    }
}
