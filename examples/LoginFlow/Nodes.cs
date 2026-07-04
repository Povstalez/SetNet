using SetNet.Config;
using SetNet.Core;

namespace LoginFlow;

/// <summary>A minimal server node (login node here). Started so the in-memory transport accepts the client.</summary>
public sealed class Node : BaseServer
{
    public Node(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new NodePeer(peerInfo);
}

/// <summary>A minimal peer (no per-peer logic needed for this demo).</summary>
public sealed class NodePeer : BasePeer
{
    public NodePeer(PeerInfo peerInfo) : base(peerInfo) { }
    protected override void OnDisconnected() { }
}

/// <summary>A minimal client.</summary>
public sealed class DemoClient : BaseClient
{
    public DemoClient(Configuration config) : base(config) { }
    protected override void OnConnected() { }
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
}
