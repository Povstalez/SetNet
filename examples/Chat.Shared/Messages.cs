using MessagePack;

namespace Chat.Shared;

/// <summary>
/// Client → server handshake message carrying the user's chosen display name.
/// Sent once, right after the connection is established.
/// </summary>
[MessagePackObject]
public class JoinMessage
{
    /// <summary>The display name the user wants to appear under in the chat.</summary>
    [Key(0)]
    public string Username { get; set; } = "";
}

/// <summary>Client → server message containing a single line of chat text the user entered.</summary>
[MessagePackObject]
public class ChatTextMessage
{
    /// <summary>The raw text the user typed.</summary>
    [Key(0)]
    public string Text { get; set; } = "";
}

/// <summary>
/// Server → client message relaying one user's chat line to everyone, with attribution and a timestamp.
/// </summary>
[MessagePackObject]
public class ChatBroadcastMessage
{
    /// <summary>Display name of the user who sent the line.</summary>
    [Key(0)]
    public string Username { get; set; } = "";

    /// <summary>The chat text being broadcast.</summary>
    [Key(1)]
    public string Text { get; set; } = "";

    /// <summary>Server send time as Unix milliseconds, so clients can render a local timestamp.</summary>
    [Key(2)]
    public long UnixTimeMs { get; set; }
}

/// <summary>
/// Server → client out-of-band notice not attributed to any user, e.g. "Alice joined" / "Bob left".
/// </summary>
[MessagePackObject]
public class SystemNoticeMessage
{
    /// <summary>The notice text to display to the user.</summary>
    [Key(0)]
    public string Text { get; set; } = "";
}
