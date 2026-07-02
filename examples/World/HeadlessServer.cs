using SetNet.Config;
using SetNet.Core;

namespace World;

/// <summary>
/// A do-nothing <see cref="BaseServer"/>. The Mobs demo runs entirely headless — no clients ever connect — but
/// <c>server.UseMobs(...)</c> hangs off a server instance, so we need one. We never call <c>StartAsync</c>: the whole
/// AI is driven by <c>mobs.Update(dtMs)</c>, which is exactly the "StateSync-optional, no connections" path.
/// </summary>
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
