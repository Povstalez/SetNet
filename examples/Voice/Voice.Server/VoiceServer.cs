using SetNet.Config;
using SetNet.Core;

namespace Voice.Server;

/// <summary>Minimal server-side peer for the voice demo (SetNet.Voice needs no per-peer logic).</summary>
public sealed class VoicePeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public VoicePeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>Minimal server that accepts clients and starts their receive loop; the voice relay hub does the rest.</summary>
public sealed class VoiceRelayServer : BaseServer
{
    /// <summary>Creates the server with the given configuration.</summary>
    public VoiceRelayServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new VoicePeer(info);
        peer.StartReceive();
        return peer;
    }
}
