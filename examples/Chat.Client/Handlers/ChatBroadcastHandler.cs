using Chat.Shared;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace Chat.Client.Handlers;

/// <summary>
/// Client-side handler for <see cref="ChatMessageTypes.ChatBroadcast"/>. Renders a chat line relayed by
/// the server, with a local timestamp and the sender's name. Auto-discovered via the
/// <see cref="MessageHandlerAttribute"/> when the client starts.
/// </summary>
[MessageHandler((ushort)ChatMessageTypes.ChatBroadcast)]
public class ChatBroadcastHandler : IClientMessageHandler<ChatBroadcastMessage>
{
    /// <summary>Prints the broadcast chat line to the console.</summary>
    /// <param name="message">The deserialized <see cref="ChatBroadcastMessage"/>.</param>
    /// <returns>A completed task (rendering is synchronous).</returns>
    public Task HandleAsync(ChatBroadcastMessage message)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(message.UnixTimeMs).LocalDateTime.ToString("HH:mm:ss");
        Console.WriteLine($"[{time}] {message.Username}: {message.Text}");
        return Task.CompletedTask;
    }
}
