using System.Collections.Concurrent;
using SetNet.Config;
using SetNet.Core;

namespace Presence.Server;

/// <summary>Minimal server-side peer.</summary>
public sealed class PresencePeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public PresencePeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>
/// Holds the pub/sub state: which peers are subscribed to which topic. The <c>PresenceService</c> reads this via
/// <c>peer.CurrentPeerInfo.Server</c>. Subscriptions are cleaned up automatically when a peer disconnects.
/// </summary>
public sealed class PresenceServer : BaseServer
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, BasePeer>> _topics = new();

    /// <summary>Creates the server and wires per-peer subscription cleanup on disconnect.</summary>
    public PresenceServer(Configuration config) : base(config)
    {
        PeerDisconnected += peer =>
        {
            foreach (var subs in _topics.Values) subs.TryRemove(peer.CurrentPeerInfo.Id, out _);
        };
    }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new PresencePeer(info);
        peer.StartReceive();
        return peer;
    }

    /// <summary>Adds a peer to a topic.</summary>
    public void Subscribe(string topic, BasePeer peer)
        => _topics.GetOrAdd(topic ?? "", _ => new ConcurrentDictionary<Guid, BasePeer>())[peer.CurrentPeerInfo.Id] = peer;

    /// <summary>Removes a peer from a topic.</summary>
    public void Unsubscribe(string topic, BasePeer peer)
    {
        if (_topics.TryGetValue(topic ?? "", out var subs)) subs.TryRemove(peer.CurrentPeerInfo.Id, out _);
    }

    /// <summary>The current subscribers of a topic.</summary>
    public IEnumerable<BasePeer> Subscribers(string topic)
        => _topics.TryGetValue(topic ?? "", out var subs) ? subs.Values.ToArray() : Array.Empty<BasePeer>();
}
