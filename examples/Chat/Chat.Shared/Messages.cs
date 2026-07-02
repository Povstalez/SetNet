using MessagePack;

namespace Chat.Shared;

/// <summary>Client → server (op <see cref="ChatOp.Join"/>): the display name the user wants to join under.</summary>
[MessagePackObject]
public class JoinRequest
{
    /// <summary>The chosen display name.</summary>
    [Key(0)] public string Username { get; set; } = "";
}

/// <summary>Server → client reply to <see cref="ChatOp.Join"/>: a welcome line and the current online count.</summary>
[MessagePackObject]
public class JoinReply
{
    /// <summary>A friendly welcome message.</summary>
    [Key(0)] public string Welcome { get; set; } = "";
    /// <summary>How many users are currently connected.</summary>
    [Key(1)] public int OnlineCount { get; set; }
}

/// <summary>Client → server (op <see cref="ChatOp.Say"/>): one line of chat text (fire-and-forget).</summary>
[MessagePackObject]
public class SayMessage
{
    /// <summary>The text the user typed.</summary>
    [Key(0)] public string Text { get; set; } = "";
}

/// <summary>Server → client (event <see cref="ChatEvt.Message"/>): a chat line relayed to everyone, attributed and timestamped.</summary>
[MessagePackObject]
public class ChatBroadcast
{
    /// <summary>Display name of the sender.</summary>
    [Key(0)] public string Username { get; set; } = "";
    /// <summary>The chat text.</summary>
    [Key(1)] public string Text { get; set; } = "";
    /// <summary>Server send time (Unix ms) for a local timestamp.</summary>
    [Key(2)] public long UnixTimeMs { get; set; }
}

/// <summary>Server → client (event <see cref="ChatEvt.Notice"/>): an out-of-band notice, e.g. "Alice joined".</summary>
[MessagePackObject]
public class SystemNotice
{
    /// <summary>The notice text.</summary>
    [Key(0)] public string Text { get; set; } = "";
}
