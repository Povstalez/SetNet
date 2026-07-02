using MessagePack;

namespace Auth.Shared;

/// <summary>
/// A tiny protected app channel used to <b>prove the auth gate</b>. The Auth package drops every application frame
/// (including this Ping) until the peer authenticates — so a successful Ping/Pong only happens <i>after</i> login.
/// App channels use ids above the shipped modules' range, so <c>110</c> is a safe pick.
/// </summary>
public static class PingChannel
{
    /// <summary>The ping channel id.</summary>
    public const ushort Id = 110;
}

/// <summary>Client → server operations on the ping channel.</summary>
public enum PingOp : ushort
{
    /// <summary>Send a ping and get a pong back (request → reply). Only works once authenticated.</summary>
    Ping = 1,
}

/// <summary>Client → server (op <see cref="PingOp.Ping"/>): a ping request carrying a note to echo.</summary>
[MessagePackObject]
public class PingRequest
{
    /// <summary>A note the server echoes back, so you can see the round-trip.</summary>
    [Key(0)] public string Note { get; set; } = "";
}

/// <summary>Server → client reply to <see cref="PingOp.Ping"/>: a pong echoing the note back.</summary>
[MessagePackObject]
public class PongReply
{
    /// <summary>Always "pong" — proof the gate is open (this frame only reaches the server after auth).</summary>
    [Key(0)] public string Reply { get; set; } = "";

    /// <summary>The note the client sent, echoed back so you can see the round-trip.</summary>
    [Key(1)] public string Echo { get; set; } = "";
}
