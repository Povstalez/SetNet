using SetNet.Auth;
using SetNet.Config;
using SetNet.Core;

namespace Auth.Server;

/// <summary>Minimal server-side peer (auth is enforced by the SetNet.Auth gate, not per-peer code).</summary>
public sealed class DemoPeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public DemoPeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>
/// A tiny server behind an <b>auth gate</b>. <c>UseAuth</c> installs an enforced inbound gate: until a peer presents
/// a valid token, all of its application frames (including the Ping channel) are dropped — only the auth handshake
/// gets through. The protected endpoint lives in <see cref="PingChannelService"/>.
/// </summary>
public sealed class DemoServer : BaseServer
{
    /// <summary>Creates the server and installs the auth gate.</summary>
    public DemoServer(Configuration config) : base(config)
    {
        this.UseAuth(new DemoAuthenticator(), new AuthOptions
        {
            MultiSession = MultiSessionPolicy.AllowMultiple,
            SessionTtl = TimeSpan.FromMinutes(2),   // reconnect-resume window (default in-memory store)
        });
    }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new DemoPeer(info);
        peer.StartReceive();
        return peer;
    }
}
