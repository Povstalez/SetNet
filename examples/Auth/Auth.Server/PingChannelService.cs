using Auth.Shared;
using SetNet.Core;
using SetNet.Protocol;

namespace Auth.Server;

/// <summary>
/// The protected app endpoint that proves the gate. This channel service is auto-discovered on the unified
/// SetNet.Protocol layer, but the Auth package drops every application frame until a peer authenticates — so this
/// handler is <b>never reached</b> by an unauthenticated client, and a client sees a <c>pong</c> only after login.
/// </summary>
[ProtocolChannel(PingChannel.Id)]
public sealed class PingChannelService
{
    /// <summary>Replies with a pong (request/reply). Only invoked once the caller has authenticated.</summary>
    [Op((ushort)PingOp.Ping)]
    public Task<PongReply> Ping(BasePeer peer, PingRequest req)
    {
        Console.WriteLine($"[server] ping from an authenticated peer: \"{req.Note}\"");
        return Task.FromResult(new PongReply { Reply = "pong", Echo = req.Note });
    }
}
