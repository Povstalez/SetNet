using Chat.Shared;
using SetNet.Config;
using SetNet.Core;

namespace Chat.Server;

/// <summary>
/// Server-side representation of one connected chat user. Carries the display name (set when they Join) and, on
/// disconnect, unregisters from the server and announces their departure to everyone else.
/// </summary>
public class ChatPeer : BasePeer
{
    private readonly ChatServer _server;

    /// <summary>The display name the user joined with; null until the Join op is handled.</summary>
    public string? Username { get; set; }

    /// <summary>Creates a chat peer bound to its connection and owning server.</summary>
    public ChatPeer(PeerInfo peerInfo, ChatServer server) : base(peerInfo) => _server = server;

    /// <inheritdoc/>
    protected override void OnDisconnected()
    {
        _server.Unregister(CurrentPeerInfo.Id);
        if (!string.IsNullOrEmpty(Username))
        {
            Console.WriteLine($"[server] {Username} disconnected");
            _ = _server.BroadcastAsync((ushort)ChatEvt.Notice, new SystemNotice { Text = $"{Username} left the chat." });
        }
    }

    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}
