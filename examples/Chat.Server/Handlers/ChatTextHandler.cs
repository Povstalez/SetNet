using Chat.Shared;
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace Chat.Server.Handlers;

/// <summary>
/// Server-side handler for <see cref="ChatMessageTypes.ChatText"/>. Relays a user's chat line to every
/// connected client (including the sender, so they see their own message) as a
/// <see cref="ChatBroadcastMessage"/>. Auto-discovered via the <see cref="MessageHandlerAttribute"/>.
/// </summary>
[MessageHandler((ushort)ChatMessageTypes.ChatText)]
public class ChatTextHandler : IServerMessageHandler
{
    /// <summary>
    /// Deserializes the chat line, attributes it to the sender's username, stamps it with the server time,
    /// and broadcasts it to all peers.
    /// </summary>
    /// <param name="peer">The peer that sent the chat line (a <see cref="ChatPeer"/>).</param>
    /// <param name="data">The MessagePack-serialized <see cref="ChatTextMessage"/> payload.</param>
    /// <returns>A task that completes once the line has been broadcast.</returns>
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<ChatTextMessage>(data);
        var chatPeer = (ChatPeer)peer;
        var username = chatPeer.Username ?? "anon";

        Console.WriteLine($"[server] {username}: {message.Text}");
        await chatPeer.Server.BroadcastAsync(
            (ushort)ChatMessageTypes.ChatBroadcast,
            new ChatBroadcastMessage
            {
                Username = username,
                Text = message.Text,
                UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
    }
}
