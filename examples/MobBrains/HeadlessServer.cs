using SetNet.Config;
using SetNet.Core;

namespace MobBrains;

/// <summary>A do-nothing server — never started. <c>UseMobs(...)</c> needs a server instance; the whole demo is driven
/// by one <c>TickScheduler</c> in-process, no sockets.</summary>
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
