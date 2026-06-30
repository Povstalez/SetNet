using Chat.Shared;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

namespace Chat.Client.Handlers;

/// <summary>
/// Client-side handler for <see cref="ChatMessageTypes.SystemNotice"/>. Prints server-generated notices
/// (joins/leaves) prefixed with an asterisk. Auto-discovered via the <see cref="MessageHandlerAttribute"/>.
/// </summary>
[MessageHandler((ushort)ChatMessageTypes.SystemNotice)]
public class SystemNoticeHandler : IClientMessageHandler
{
    /// <summary>Deserializes and prints the system notice to the console.</summary>
    /// <param name="data">The MessagePack-serialized <see cref="SystemNoticeMessage"/> payload.</param>
    /// <returns>A completed task (rendering is synchronous).</returns>
    public Task HandleAsync(byte[] data)
    {
        var message = SetNetSerializer.Deserialize<SystemNoticeMessage>(data);
        Console.WriteLine($"* {message.Text}");
        return Task.CompletedTask;
    }
}
