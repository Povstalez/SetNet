using System.Collections.Concurrent;
using Chat.Shared;
using SetNet.Config;
using SetNet.Core;

namespace Chat.Server;

/// <summary>
/// The chat server. Extends <see cref="BaseServer"/> to accept connections, wrap each one in a
/// <see cref="ChatPeer"/>, and keep a live registry of connected peers so messages can be broadcast
/// to everyone. This is the example's hub: handlers reach it through the peer to relay chat lines.
/// </summary>
public class ChatServer : BaseServer
{
    /// <summary>All currently connected peers, keyed by their unique peer id, for broadcasting and bookkeeping.</summary>
    private readonly ConcurrentDictionary<Guid, ChatPeer> _peers = new();

    /// <summary>Creates the chat server with the given network configuration.</summary>
    /// <param name="config">Host/port and transport settings the server binds and listens on.</param>
    public ChatServer(Configuration config) : base(config) { }

    /// <summary>
    /// Factory hook invoked by the base server for every accepted client. Builds a <see cref="ChatPeer"/>
    /// (handing it a back-reference to this server so it can broadcast), starts its receive loop, and
    /// registers it in <see cref="_peers"/>.
    /// </summary>
    /// <param name="peerInfo">Per-connection metadata (id, transport connection, config) for the new client.</param>
    /// <returns>The created <see cref="ChatPeer"/> that will handle this client's traffic.</returns>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new ChatPeer(peerInfo, this);
        _peers[peerInfo.Id] = peer;
        peer.StartReceive();
        return peer;
    }

    /// <summary>Removes a peer from the registry, e.g. when it disconnects.</summary>
    /// <param name="id">The unique id of the peer to drop.</param>
    public void Unregister(Guid id) => _peers.TryRemove(id, out _);

    /// <summary>
    /// Sends a message to every connected peer, optionally skipping one (typically the original sender).
    /// Sends are best-effort: a peer that is mid-disconnect is skipped rather than failing the broadcast.
    /// </summary>
    /// <typeparam name="T">The MessagePack-serializable message type being broadcast.</typeparam>
    /// <param name="type">The wire message-type id the recipients' handlers are registered for.</param>
    /// <param name="message">The message payload to deliver to every peer.</param>
    /// <param name="except">Optional peer id to exclude from the broadcast (e.g. the sender).</param>
    /// <returns>A task that completes once delivery to all targeted peers has been attempted.</returns>
    public async Task BroadcastAsync<T>(ushort type, T message, Guid? except = null)
    {
        foreach (var kv in _peers)
        {
            if (except.HasValue && kv.Key == except.Value) continue;
            try { await kv.Value.SendMessageAsync(type, message); }
            catch { /* peer is disconnecting; skip it */ }
        }
    }
}
