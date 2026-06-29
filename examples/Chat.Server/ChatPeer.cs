using Chat.Shared;
using SetNet.Config;
using SetNet.Core;

namespace Chat.Server;

/// <summary>
/// Server-side representation of one connected chat user. Extends <see cref="BasePeer"/> to carry the
/// user's display name, expose a public send method to handlers/broadcasts, and announce departures.
/// </summary>
public class ChatPeer : BasePeer
{
    /// <summary>Back-reference to the owning server, used to broadcast and to unregister on disconnect.</summary>
    private readonly ChatServer _server;

    /// <summary>The display name the user joined with; <c>null</c> until the Join message is processed.</summary>
    public string? Username { get; set; }

    /// <summary>The server that owns this peer, so message handlers can relay to all peers.</summary>
    public ChatServer Server => _server;

    /// <summary>This peer's unique id (exposes the protected peer-info id so external handlers can address it).</summary>
    public Guid Id => CurrentPeerInfo.Id;

    /// <summary>Creates a chat peer bound to its connection and owning server.</summary>
    /// <param name="peerInfo">Per-connection metadata supplied by the base server.</param>
    /// <param name="server">The owning <see cref="ChatServer"/> for broadcasting and registry cleanup.</param>
    public ChatPeer(PeerInfo peerInfo, ChatServer server) : base(peerInfo)
    {
        _server = server;
    }

    /// <summary>
    /// Public wrapper over the protected <see cref="BasePeer.SendAsync{T}(ushort, T)"/> so the server and
    /// handlers (which live outside this class) can send messages to this specific client.
    /// </summary>
    /// <typeparam name="T">The MessagePack-serializable message type to send.</typeparam>
    /// <param name="type">The wire message-type id the client's handler is registered for.</param>
    /// <param name="message">The message payload to send to this client.</param>
    /// <returns>A task that completes when the message has been handed to the transport.</returns>
    public Task SendMessageAsync<T>(ushort type, T message) => SendAsync(type, message);

    /// <summary>
    /// Called when this client's connection ends (graceful, kicked, or lost). Removes the peer from the
    /// server registry and, if the user had joined, broadcasts a "left" notice to everyone else.
    /// </summary>
    protected override void OnDisconnected()
    {
        _server.Unregister(CurrentPeerInfo.Id);
        if (!string.IsNullOrEmpty(Username))
        {
            Console.WriteLine($"[server] {Username} disconnected ({CurrentPeerInfo.Id})");
            _ = _server.BroadcastAsync(
                (ushort)ChatMessageTypes.SystemNotice,
                new SystemNoticeMessage { Text = $"{Username} left the chat." });
        }
    }

    /// <summary>Logs unexpected transport errors for this peer to the server console.</summary>
    /// <param name="error">Human-readable description of the error that occurred.</param>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}
