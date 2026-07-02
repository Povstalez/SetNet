using Chat.Shared;
using SetNet.Config;
using SetNet.Core;
using SetNet.Protocol;

namespace Chat.Client;

/// <summary>
/// The chat client. On connect it <b>joins via a request/reply</b> (<c>RequestAsync</c>, awaiting a welcome), and
/// sends lines <b>fire-and-forget</b> (<c>PostAsync</c>). Incoming pushes are handled declaratively by
/// <see cref="ChatEventHandlers"/> (the <c>[Event]</c> analog of <c>client.On&lt;T&gt;</c>) — so this class only
/// deals with connection lifecycle and outbound sends.
/// </summary>
public class ChatClient : BaseClient
{
    private readonly string _username;

    /// <summary>Creates a chat client with its connection settings and chosen display name.</summary>
    public ChatClient(Configuration config, string username) : base(config) => _username = username;

    /// <summary>Sends a chat line (fire-and-forget over the chat channel).</summary>
    public Task SayAsync(string text)
        => this.PostAsync(ChatProtocol.Channel, (ushort)ChatOp.Say, new SayMessage { Text = text });

    /// <summary>Joins by sending a request and awaiting the server's welcome reply.</summary>
    private async Task JoinAsync()
    {
        try
        {
            var reply = await this.RequestAsync<JoinRequest, JoinReply>(
                ChatProtocol.Channel, (ushort)ChatOp.Join, new JoinRequest { Username = _username });
            Console.WriteLine($"[client] {reply.Welcome}  ({reply.OnlineCount} online)");
        }
        catch (Exception ex) { Console.WriteLine($"[client] join failed: {ex.Message}"); }
    }

    /// <inheritdoc/>
    protected override void OnConnected() { Console.WriteLine("[client] connected"); _ = JoinAsync(); }
    /// <inheritdoc/>
    protected override void OnDisconnected() => Console.WriteLine("[client] disconnected");
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");
    /// <inheritdoc/>
    protected override void OnReconnecting(int attempt, int maxAttempts)
        => Console.WriteLine($"[client] reconnecting {attempt}/{maxAttempts}...");
    /// <inheritdoc/>
    protected override void OnReconnected() { Console.WriteLine("[client] reconnected"); _ = JoinAsync(); }
}
