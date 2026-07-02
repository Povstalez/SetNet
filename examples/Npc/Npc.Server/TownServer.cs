using SetNet.Config;
using SetNet.Core;

namespace Npc.Server;

/// <summary>A minimal server-side peer — SetNet.NPC routes interact/enter-zone commands automatically, so no custom logic here.</summary>
public sealed class TownPeer : BasePeer
{
    public TownPeer(PeerInfo peerInfo) : base(peerInfo) { }
    protected override void OnDisconnected() => Console.WriteLine($"[server] peer disconnected: {CurrentPeerInfo.Id}");
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>A minimal server: it just accepts connections. All NPC behaviour comes from <c>server.UseNpc()</c> + the registered behaviours in <c>Program.cs</c>.</summary>
public sealed class TownServer : BaseServer
{
    public TownServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new TownPeer(peerInfo);
}
