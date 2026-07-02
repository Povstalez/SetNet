using System.Collections.Concurrent;
using Chat.Shared;
using SetNet.Config;
using SetNet.Core;
using SetNet.Protocol;

namespace Chat.Server;

/// <summary>
/// The chat server. Extends <see cref="BaseServer"/> to accept connections, wrap each in a <see cref="ChatPeer"/>,
/// and keep a live registry of connected peers. Chat traffic rides the unified <c>SetNet.Protocol</c>: the
/// <see cref="ChatChannelService"/> handles inbound ops, and this hub fans events out to everyone via
/// <c>PublishAsync</c> — no bespoke wire types or broadcast plumbing.
/// </summary>
public class ChatServer : BaseServer
{
    private readonly ConcurrentDictionary<Guid, ChatPeer> _peers = new();

    /// <summary>Creates the chat server with the given network configuration.</summary>
    public ChatServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new ChatPeer(peerInfo, this);
        _peers[peerInfo.Id] = peer;
        peer.StartReceive();
        return peer;
    }

    /// <summary>Drops a peer from the registry (called on disconnect).</summary>
    public void Unregister(Guid id) => _peers.TryRemove(id, out _);

    /// <summary>How many users are currently connected.</summary>
    public int OnlineCount => _peers.Count;

    /// <summary>
    /// Pushes a chat event to every connected peer (optionally excluding one), on the chat channel. Best-effort:
    /// a dropping peer is skipped by the underlying <c>IEnumerable&lt;BasePeer&gt;.PublishAsync</c> helper.
    /// </summary>
    public Task BroadcastAsync<T>(ushort evt, T message, BasePeer? except = null)
    {
        IEnumerable<BasePeer> targets = _peers.Values;
        if (except != null) targets = targets.Where(p => p.CurrentPeerInfo.Id != except.CurrentPeerInfo.Id);
        return targets.PublishAsync(ChatProtocol.Channel, evt, message);
    }
}
