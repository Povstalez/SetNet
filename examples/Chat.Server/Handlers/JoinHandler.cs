using Chat.Shared;
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace Chat.Server.Handlers;

/// <summary>
/// Server-side handler for <see cref="ChatMessageTypes.Join"/>. Records the user's display name on
/// their peer and announces the arrival to everyone. Auto-discovered and registered via the
/// <see cref="MessageHandlerAttribute"/> at server start.
/// </summary>
[MessageHandler((ushort)ChatMessageTypes.Join)]
public class JoinHandler : IServerMessageHandler
{
    /// <summary>
    /// Deserializes the join request, stores a sanitized username on the sending <see cref="ChatPeer"/>,
    /// and broadcasts a "joined" system notice to all connected users.
    /// </summary>
    /// <param name="peer">The peer that sent the join message (a <see cref="ChatPeer"/>).</param>
    /// <param name="data">The MessagePack-serialized <see cref="JoinMessage"/> payload.</param>
    /// <returns>A task that completes once the join notice has been broadcast.</returns>
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<JoinMessage>(data);
        var chatPeer = (ChatPeer)peer;
        chatPeer.Username = string.IsNullOrWhiteSpace(message.Username) ? "anon" : message.Username.Trim();

        Console.WriteLine($"[server] {chatPeer.Username} joined ({chatPeer.Id})");
        await chatPeer.Server.BroadcastAsync(
            (ushort)ChatMessageTypes.SystemNotice,
            new SystemNoticeMessage { Text = $"{chatPeer.Username} joined the chat." });
    }
}
