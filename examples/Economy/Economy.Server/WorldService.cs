using Economy.Shared;
using SetNet.Core;
using SetNet.Protocol;
using SetNet.Rooms;

namespace Economy.Server;

/// <summary>
/// The item-drop action. Authoritative: the server takes the item from the dropper's inventory
/// (<see cref="SetNet.Inventory.InventoryServer.TryRevokeAsync"/>) and, on success, pushes an <c>ItemDropped</c>
/// event to the <b>other members of the dropper's room</b> using the built-in <c>BroadcastToRoomOfAsync</c> helper —
/// no hand-maintained membership map. Combines Rooms + Inventory + a custom protocol channel on one connection.
/// </summary>
[ProtocolChannel(WorldChannel.Id)]
public sealed class WorldService
{
    /// <summary>Handles a drop (fire-and-forget from the client).</summary>
    [Op((ushort)WorldOp.Drop)]
    public async Task Drop(BasePeer peer, DropReq req)
    {
        var server = (GameServer)peer.CurrentPeerInfo.Server!;
        var key = server.Inventory.KeyOf(peer);

        if (!await server.Inventory.TryRevokeAsync(key, req.ItemId, req.Count))
        {
            Console.WriteLine($"[server] {key} can't drop {req.Count}x{req.ItemId} (not enough)");
            return;
        }

        Console.WriteLine($"[server] {key} dropped {req.Count}x{req.ItemId}");
        await server.BroadcastToRoomOfAsync(peer, WorldChannel.Id, (ushort)WorldEvt.ItemDropped,
            new ItemDropped { PlayerId = key, ItemId = req.ItemId, Count = req.Count });
    }
}
