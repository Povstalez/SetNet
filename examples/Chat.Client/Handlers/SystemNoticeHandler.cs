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
public class SystemNoticeHandler : IClientMessageHandler<SystemNoticeMessage>
{
    /// <summary>Prints the system notice to the console.</summary>
    /// <param name="message">The deserialized <see cref="SystemNoticeMessage"/>.</param>
    /// <returns>A completed task (rendering is synchronous).</returns>
    public Task HandleAsync(SystemNoticeMessage message)
    {
        Console.WriteLine($"* {message.Text}");
        return Task.CompletedTask;
    }
}
