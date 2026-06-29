namespace Chat.Shared;

/// <summary>
/// Wire message-type identifiers shared by the chat client and server. Each value is the
/// <c>ushort</c> type id passed to <c>SendAsync</c> and matched by a <c>[MessageHandler(...)]</c>.
/// Keeping them in the shared project guarantees both ends agree on the protocol.
/// </summary>
public enum ChatMessageTypes : ushort
{
    /// <summary>Client → server: announces the user's display name when joining.</summary>
    Join = 1,

    /// <summary>Client → server: a chat line the user typed.</summary>
    ChatText = 2,

    /// <summary>Server → client: a chat line relayed to every connected user.</summary>
    ChatBroadcast = 3,

    /// <summary>Server → client: a server-generated notice (e.g. someone joined or left).</summary>
    SystemNotice = 4
}
