using SetNet.Config;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Trade;
using Trade.Shared;

namespace Trade.Server;

/// <summary>Minimal server-side peer (all trade logic lives in the SetNet.Trade hub).</summary>
public sealed class DemoPeer : BasePeer
{
    /// <summary>Creates the peer from the accepted connection info.</summary>
    public DemoPeer(PeerInfo info) : base(info) { }
    /// <inheritdoc/>
    protected override void OnDisconnected() { }
    /// <inheritdoc/>
    protected override void OnError(string error) => Console.WriteLine($"[server] peer error: {error}");
}

/// <summary>
/// A tiny escrow-trade server: <b>Inventory</b> holds authoritative items, <b>Trade</b> drives the two-phase
/// swap (both ready → both confirm → atomic cross-grant). On connect each player gets a starter kit so there is
/// something to trade, and the server prints that player's <b>inventory key</b> — copy it into the other client's
/// <c>propose</c> command to start a trade.
/// </summary>
public sealed class DemoServer : BaseServer
{
    /// <summary>The authoritative inventory hub (shared with the trade hub).</summary>
    public InventoryServer Inventory { get; }

    /// <summary>Creates the server and wires inventory + trade.</summary>
    public DemoServer(Configuration config) : base(config)
    {
        Inventory = this.UseInventory();
        this.UseTrade(Inventory);   // trade swaps items through this same inventory hub

        PeerConnected += peer =>
        {
            var key = Inventory.KeyOf(peer);
            // Give the connecting player a starter kit so there's something to put on the table.
            foreach (var (itemId, count) in Starter.Kit)
                _ = Inventory.GrantAsync(key, itemId, count);
            // Print the key so the demo user can copy it into the OTHER client's `propose <key>`.
            Console.WriteLine($"[server] player key: {key}");
        };
    }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo info)
    {
        var peer = new DemoPeer(info);
        peer.StartReceive();
        return peer;
    }
}
