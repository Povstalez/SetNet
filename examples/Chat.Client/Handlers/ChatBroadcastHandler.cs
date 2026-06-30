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
public class ChatBroadcastHandler : IClientMessageHandler
{
    /// <summary>Deserializes and prints the broadcast chat line to the console.</summary>
    /// <param name="data">The MessagePack-serialized <see cref="ChatBroadcastMessage"/> payload.</param>
    /// <returns>A completed task (rendering is synchronous).</returns>
    public Task HandleAsync(byte[] data)
    {
        var message = SetNetSerializer.Deserialize<ChatBroadcastMessage>(data);
        var time = DateTimeOffset.FromUnixTimeMilliseconds(message.UnixTimeMs).LocalDateTime.ToString("HH:mm:ss");
        Console.WriteLine($"[{time}] {message.Username}: {message.Text}");
        return Task.CompletedTask;
    }
}
