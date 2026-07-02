using SetNet.Config;
using SetNet.Core;

namespace Rooms.Server;

/// <summary>
/// A minimal server-side peer. SetNet.Rooms handles all room membership and broadcast relaying, so this peer needs no
/// custom logic — the framework routes room commands automatically.
/// </summary>
public sealed class LobbyPeer : BasePeer
{
    public LobbyPeer(PeerInfo peerInfo) : base(peerInfo) { }

    protected override void OnDisconnected() { Console.WriteLine($"[server] peer disconnected: {CurrentPeerInfo.Id}"); }
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>
/// A minimal server: it just accepts connections. Everything room-related (create/join by code, broadcast, join/leave
/// events, auto-leave on disconnect) is added by <c>server.UseRooms()</c> in <c>Program.cs</c> — no room code here.
/// </summary>
public sealed class LobbyServer : BaseServer
{
    public LobbyServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new LobbyPeer(peerInfo);
}
