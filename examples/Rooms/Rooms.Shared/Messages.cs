using MessagePack;

namespace Rooms.Shared;

/// <summary>
/// Message-type ids for typed room broadcasts. These are the <c>ushort</c> tag passed to
/// <c>rooms.BroadcastAsync&lt;T&gt;(messageType, msg)</c> and matched by <c>rooms.On&lt;T&gt;(messageType, …)</c> — a room
/// channel can carry several message types, each routed to its own typed handler.
/// </summary>
public enum RoomMsg : ushort
{
    /// <summary>A line of chat text broadcast to the room.</summary>
    ChatLine = 1,
}

/// <summary>A chat line broadcast within a room.</summary>
[MessagePackObject]
public class RoomChatLine
{
    /// <summary>The text the sender typed.</summary>
    [Key(0)] public string Text { get; set; } = "";
}
