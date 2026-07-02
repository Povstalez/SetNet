using Chat.Shared;
using SetNet.Protocol;

namespace Chat.Client;

/// <summary>
/// Declarative client-side push handlers — the <c>[Event]</c> analog of imperative <c>client.On&lt;T&gt;</c>. This
/// class is auto-discovered and its methods auto-subscribed the first time a chat event arrives, so incoming
/// messages/notices print without any manual wiring. (For a handler that must close over per-instance state you'd
/// use <c>client.On&lt;T&gt;(ChatProtocol.Channel, op, …)</c> instead; both styles coexist.)
/// </summary>
[ProtocolChannel(ChatProtocol.Channel)]
public sealed class ChatEventHandlers
{
    /// <summary>Prints a relayed chat line with a local timestamp.</summary>
    [Event((ushort)ChatEvt.Message)]
    public void OnMessage(ChatBroadcast m)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(m.UnixTimeMs).LocalDateTime.ToString("HH:mm:ss");
        Console.WriteLine($"[{time}] {m.Username}: {m.Text}");
    }

    /// <summary>Prints a server notice (join/leave).</summary>
    [Event((ushort)ChatEvt.Notice)]
    public void OnNotice(SystemNotice n) => Console.WriteLine($"* {n.Text}");
}
