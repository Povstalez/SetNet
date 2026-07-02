namespace Chat.Shared;

/// <summary>
/// The chat demo rides the unified <c>SetNet.Protocol</c> on its own application channel. App channels use ids
/// above the shipped modules' range (1–25), so <c>100</c> is a safe pick. Within the channel, ops identify
/// client→server commands and events identify server→client pushes.
/// </summary>
public static class ChatProtocol
{
    /// <summary>The chat channel id (demultiplexes chat traffic on the shared protocol envelope).</summary>
    public const ushort Channel = 100;
}

/// <summary>Client → server operations on the chat channel.</summary>
public enum ChatOp : ushort
{
    /// <summary>Announce a display name and get a welcome (request → reply).</summary>
    Join = 1,
    /// <summary>Send a chat line (fire-and-forget).</summary>
    Say = 2,
}

/// <summary>Server → client push events on the chat channel.</summary>
public enum ChatEvt : ushort
{
    /// <summary>A chat line relayed to everyone.</summary>
    Message = 10,
    /// <summary>A server notice (someone joined/left).</summary>
    Notice = 11,
}
