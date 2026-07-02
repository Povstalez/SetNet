using SetNet.Config;
using SetNet.Core;

namespace Party.Server;

/// <summary>Minimal server-side peer for the demo (parties need no per-peer logic).</summary>
public sealed class DemoPeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public DemoPeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>Minimal server that accepts clients and starts their receive loop; the party layer does the rest.</summary>
public sealed class DemoServer : BaseServer
{
    /// <summary>Creates the server with the given configuration.</summary>
    public DemoServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new DemoPeer(info);
        peer.StartReceive();
        return peer;
    }
}
