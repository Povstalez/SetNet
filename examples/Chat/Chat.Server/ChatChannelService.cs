using Chat.Shared;
using SetNet.Core;
using SetNet.Protocol;

namespace Chat.Server;

/// <summary>
/// Server-side chat handling on the unified protocol — one method per op (auto-discovered, no <c>switch</c>, no
/// <c>[MessageHandler]</c>). <see cref="Join"/> is a request/reply (returns a welcome); <see cref="Say"/> is
/// fire-and-forget. Both fan events out to the room via <see cref="ChatServer.BroadcastAsync{T}"/>.
/// </summary>
[ProtocolChannel(ChatProtocol.Channel)]
public sealed class ChatChannelService
{
    /// <summary>Registers the display name, announces the arrival to others, and replies with a welcome.</summary>
    [Op((ushort)ChatOp.Join)]
    public async Task<JoinReply> Join(BasePeer peer, JoinRequest req)
    {
        var server = (ChatServer)peer.CurrentPeerInfo.Server!;
        ((ChatPeer)peer).Username = req.Username;
        Console.WriteLine($"[server] {req.Username} joined");

        await server.BroadcastAsync((ushort)ChatEvt.Notice,
            new SystemNotice { Text = $"{req.Username} joined the chat." }, except: peer);

        return new JoinReply { Welcome = $"Welcome, {req.Username}!", OnlineCount = server.OnlineCount };
    }

    /// <summary>Relays a chat line to everyone (including the sender, so they see it with the server timestamp).</summary>
    [Op((ushort)ChatOp.Say)]
    public Task Say(BasePeer peer, SayMessage req)
    {
        var server = (ChatServer)peer.CurrentPeerInfo.Server!;
        var name = ((ChatPeer)peer).Username ?? "?";
        var evt = new ChatBroadcast
        {
            Username = name,
            Text = req.Text,
            UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return server.BroadcastAsync((ushort)ChatEvt.Message, evt);
    }
}
