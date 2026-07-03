using SetNet.Config;
using SetNet.Core;

namespace UnifiedMove;

/// <summary>A do-nothing server — we never start it. `UseMobs(...)` needs a server instance; the whole demo is driven
/// by ticking Locomotion + Mobs directly (no networking), so we can watch the unified movement in one process.</summary>
public sealed class HeadlessServer : BaseServer
{
    public HeadlessServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new HeadlessPeer(peerInfo);
}

public sealed class HeadlessPeer : BasePeer
{
    public HeadlessPeer(PeerInfo peerInfo) : base(peerInfo) { }
    protected override void OnDisconnected() { }
}
