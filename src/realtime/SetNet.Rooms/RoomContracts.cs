using System;
using System.Collections.Generic;

namespace SetNet.Rooms
{
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
