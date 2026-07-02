using SetNet.Config;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Rooms;

namespace Economy.Server;

/// <summary>Minimal server-side peer.</summary>
public sealed class GamePeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public GamePeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>
/// A tiny dedicated game server: <b>Rooms</b> for grouping (unlimited rooms) + <b>Inventory</b> for authoritative
/// items. Grants each player some starter items on connect. The item-drop action lives in <c>WorldService</c>.
/// </summary>
public sealed class GameServer : BaseServer
{
    /// <summary>The authoritative inventory hub.</summary>
    public InventoryServer Inventory { get; }

    /// <summary>Creates the server and wires rooms + inventory.</summary>
    public GameServer(Configuration config) : base(config)
    {
        this.UseRooms(new MemoryRoomStore());
        Inventory = this.UseInventory(new MemoryInventoryStore());
        // Give every player 5 swords when they connect (so there's something to drop).
        PeerConnected += peer => _ = Inventory.GrantAsync(Inventory.KeyOf(peer), "sword", 5);
    }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new GamePeer(info);
        peer.StartReceive();
        return peer;
    }
}
