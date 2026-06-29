using Chat.Shared;
using SetNet.Config;
using SetNet.Core;

namespace Chat.Client;

/// <summary>
/// The chat client. Extends <see cref="BaseClient"/> to send the join handshake on connect and to
/// expose a simple <see cref="SendChatAsync"/> helper for the console UI. Incoming traffic is handled
/// by the auto-discovered client handlers (broadcast + system notice), which print to the console.
/// </summary>
public class ChatClient : BaseClient
{
    /// <summary>The display name announced to the server on connect.</summary>
    private readonly string _username;

    /// <summary>Creates a chat client with its connection settings and chosen display name.</summary>
    /// <param name="config">Host/port and transport settings used to reach the server.</param>
    /// <param name="username">The display name sent in the join message and shown to other users.</param>
    public ChatClient(Configuration config, string username) : base(config)
    {
        _username = username;
    }

    /// <summary>Sends a line of chat text to the server for broadcasting.</summary>
    /// <param name="text">The message text the user typed.</param>
    /// <returns>A task that completes once the message has been handed to the transport.</returns>
    public Task SendChatAsync(string text)
        => SendAsync((ushort)ChatMessageTypes.ChatText, new ChatTextMessage { Text = text });

    /// <summary>
    /// Fired once the connection is established. Sends the <see cref="JoinMessage"/> so the server can
    /// register the username and announce the arrival.
    /// </summary>
    protected override void OnConnected()
    {
        Console.WriteLine("[client] connected");
        _ = SendAsync((ushort)ChatMessageTypes.Join, new JoinMessage { Username = _username });
    }

    /// <summary>Fired when the connection closes (intentionally or after reconnects are exhausted).</summary>
    protected override void OnDisconnected() => Console.WriteLine("[client] disconnected");

    /// <summary>Fired on an unexpected transport error.</summary>
    /// <param name="error">Human-readable description of the error.</param>
    protected override void OnError(string error) => Console.WriteLine($"[client] error: {error}");

    /// <summary>Fired before each automatic reconnect attempt.</summary>
    /// <param name="attempt">The 1-based attempt number.</param>
    /// <param name="maxAttempts">The configured maximum number of attempts.</param>
    protected override void OnReconnecting(int attempt, int maxAttempts)
        => Console.WriteLine($"[client] reconnecting {attempt}/{maxAttempts}...");

    /// <summary>Fired after a successful reconnect; re-sends the join so the server re-registers this user.</summary>
    protected override void OnReconnected()
    {
        Console.WriteLine("[client] reconnected");
        _ = SendAsync((ushort)ChatMessageTypes.Join, new JoinMessage { Username = _username });
    }
}
