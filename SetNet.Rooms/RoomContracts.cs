using System;
using System.Collections.Generic;

namespace SetNet.Rooms
{
    /// <summary>Reserved wire type ids for the rooms protocol. Below the auth (65529/65530), RPC and system ranges. Don't reuse these ids.</summary>
    public static class RoomTypes
    {
        /// <summary>Client → server command (create/join/leave/broadcast).</summary>
        public const ushort Command = ushort.MaxValue - 8;   // 65527

        /// <summary>Server → client reply to a command (correlated).</summary>
        public const ushort Reply = ushort.MaxValue - 7;     // 65528

        /// <summary>Server → client push event (player joined/left, message, closed).</summary>
        public const ushort Event = ushort.MaxValue - 9;     // 65526
    }

    internal enum RoomOp : byte { Create = 0, Join = 1, Leave = 2, Broadcast = 3 }

    internal enum RoomEventType : byte { PlayerJoined = 0, PlayerLeft = 1, Message = 2, Closed = 3 }

    /// <summary>Options for creating a room.</summary>
    public sealed class RoomOptions
    {
        /// <summary>Maximum players allowed in the room (including the creator). 0 = unlimited.</summary>
        public int MaxPlayers { get; set; }
    }

    /// <summary>What a client knows about a room it created or joined.</summary>
    public sealed class RoomInfo
    {
        /// <summary>The room's join code.</summary>
        public string Code { get; }

        /// <summary>This client's own player id within the room.</summary>
        public string OwnPlayerId { get; }

        /// <summary>The current members' player ids (including you).</summary>
        public IReadOnlyList<string> Members { get; }

        /// <summary>Creates a room info snapshot.</summary>
        public RoomInfo(string code, string ownPlayerId, IReadOnlyList<string> members)
        {
            Code = code;
            OwnPlayerId = ownPlayerId;
            Members = members;
        }
    }

    /// <summary>Thrown on the client when a room command is rejected (room full, not found, …).</summary>
    public class RoomException : Exception
    {
        /// <summary>Creates a <see cref="RoomException"/> with the server's reason.</summary>
        public RoomException(string message) : base(message) { }
    }
}
