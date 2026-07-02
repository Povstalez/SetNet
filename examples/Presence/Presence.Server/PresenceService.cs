using Presence.Shared;
using SetNet.Core;
using SetNet.Protocol;

namespace Presence.Server;

/// <summary>
/// The pub/sub logic — one method per op (auto-discovered, no <c>switch</c>). Subscribe/Unsubscribe update the
/// server's topic map; Publish fans the message out to that topic's subscribers via <c>PublishAsync</c>. A complete
/// custom feature built on the primitive, no companion module.
/// </summary>
[ProtocolChannel(PresenceChannel.Id)]
public sealed class PresenceService
{
    /// <summary>Subscribe the caller to a topic (fire-and-forget).</summary>
    [Op((ushort)PresenceOp.Subscribe)]
    public void Subscribe(BasePeer peer, TopicRef req)
        => ((PresenceServer)peer.CurrentPeerInfo.Server!).Subscribe(req.Topic, peer);

    /// <summary>Unsubscribe the caller from a topic (fire-and-forget).</summary>
    [Op((ushort)PresenceOp.Unsubscribe)]
    public void Unsubscribe(BasePeer peer, TopicRef req)
        => ((PresenceServer)peer.CurrentPeerInfo.Server!).Unsubscribe(req.Topic, peer);

    /// <summary>Publish text to a topic — delivered to every current subscriber.</summary>
    [Op((ushort)PresenceOp.Publish)]
    public Task Publish(BasePeer peer, PublishReq req)
    {
        var server = (PresenceServer)peer.CurrentPeerInfo.Server!;
        var msg = new TopicMessage
        {
            Topic = req.Topic,
            From = peer.CurrentPeerInfo.Id.ToString("N").Substring(0, 8),
            Text = req.Text,
        };
        return server.Subscribers(req.Topic).PublishAsync(PresenceChannel.Id, (ushort)PresenceEvt.Message, msg);
    }
}
