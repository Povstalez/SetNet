using System;
using System.Collections.Generic;

namespace SetNet.Matchmaking
{
    /// <summary>
    /// Reserved wire type ids for the matchmaking protocol. These sit just below the rooms range
    /// (65526/65527/65528) so the two packages can be used together without collisions. Don't reuse these ids.
    /// </summary>
    public static class MatchTypes
    {
        /// <summary>Server → client push event (a match was found for this player).</summary>
        public const ushort Event = ushort.MaxValue - 12;     // 65523

        /// <summary>Client → server command (enqueue / cancel).</summary>
        public const ushort Command = ushort.MaxValue - 11;   // 65524

        /// <summary>Server → client reply to a command (correlated).</summary>
        public const ushort Reply = ushort.MaxValue - 10;     // 65525
    }

    internal enum MatchOp : byte { Enqueue = 0, Cancel = 1 }

    /// <summary>
    /// Tunables for the server-side matchmaker. Out of the box (<see cref="UseSkill"/> = false) it forms matches by
    /// simple FIFO grouping within each queue; enable skill-based matching to group players of similar rating with an
    /// acceptance window that widens the longer a player waits (so nobody is stuck forever).
    /// </summary>
    public sealed class MatchmakingOptions
    {
        /// <summary>How many players form one match. Default 2.</summary>
        public int MatchSize { get; set; } = 2;

        /// <summary>When true, only group players whose skill ratings fall within the (widening) acceptance window. Default false (pure FIFO).</summary>
        public bool UseSkill { get; set; } = false;

        /// <summary>The initial ± skill spread a group may span, at the moment a player enters the queue. Default 100.</summary>
        public double BaseSkillWindow { get; set; } = 100;

        /// <summary>How much the acceptance window grows per second a player waits, so long waits eventually match anyone. Default 50/s.</summary>
        public double SkillWindowGrowthPerSecond { get; set; } = 50;

        /// <summary>How often the matchmaker tries to form matches, in milliseconds. Default 500.</summary>
        public int TickIntervalMs { get; set; } = 500;

        /// <summary>Capacity of the room created for a formed match. 0 (default) uses <see cref="MatchSize"/>.</summary>
        public int MatchedRoomMaxPlayers { get; set; } = 0;
    }

    /// <summary>A request to join the matchmaking queue.</summary>
    public sealed class MatchRequest
    {
        /// <summary>The queue/pool to join (e.g. mode+region like "ranked-eu"). Only players in the same queue match together.</summary>
        public string Queue { get; set; } = "default";

        /// <summary>This player's skill rating, used only when the server has <see cref="MatchmakingOptions.UseSkill"/> enabled.</summary>
        public int Skill { get; set; }
    }

    /// <summary>The outcome of a successful match: the room to join and who you were matched with.</summary>
    public sealed class MatchResult
    {
        /// <summary>The queue this match came from.</summary>
        public string Queue { get; }

        /// <summary>The join code of the room created for this match — join it via your <c>RoomsClient</c>.</summary>
        public string RoomCode { get; }

        /// <summary>The player ids of everyone in the match (including you).</summary>
        public IReadOnlyList<string> Players { get; }

        /// <summary>Your own player id within the match.</summary>
        public string OwnPlayerId { get; }

        /// <summary>Creates a match result.</summary>
        public MatchResult(string queue, string roomCode, IReadOnlyList<string> players, string ownPlayerId)
        {
            Queue = queue ?? "";
            RoomCode = roomCode ?? "";
            Players = players ?? Array.Empty<string>();
            OwnPlayerId = ownPlayerId ?? "";
        }
    }

    /// <summary>Thrown on the client when a matchmaking command is rejected by the server.</summary>
    public class MatchmakingException : Exception
    {
        /// <summary>Creates a <see cref="MatchmakingException"/> with the server's reason.</summary>
        public MatchmakingException(string message) : base(message) { }
    }
}
