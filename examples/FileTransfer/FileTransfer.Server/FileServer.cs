using SetNet.Config;
using SetNet.Core;

namespace FileTransfer.Server;

/// <summary>Minimal server-side peer for the file-transfer demo (SetNet.Streams needs no per-peer logic).</summary>
public sealed class FilePeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public FilePeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>Minimal server that accepts clients and starts their receive loop; the streaming hub does the rest.</summary>
public sealed class FileServer : BaseServer
{
    /// <summary>Creates the server with the given configuration.</summary>
    public FileServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new FilePeer(info);
        peer.StartReceive();
        return peer;
    }
}
