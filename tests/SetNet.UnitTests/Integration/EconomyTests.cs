using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Auction;
using SetNet.Config;
using SetNet.Core;
using SetNet.Crafting;
using SetNet.InMemory;
using SetNet.Inventory;
using SetNet.Loot;
using SetNet.Vendor;
using SetNet.Wallet;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for the economy stack (Wallet / Vendor / Crafting / Loot / Auction), all built on the shared
/// Inventory + Wallet hubs. A deterministic connect-order player key lets two clients address each other.
/// </summary>
[Collection("integration")]
public class EconomyTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    private static Func<BasePeer, string> OrderedKeys(BaseServer server)
    {
        var map = new ConcurrentDictionary<Guid, string>();
        var counter = 0;
        server.PeerConnected += peer => map[peer.CurrentPeerInfo.Id] = $"player{System.Threading.Interlocked.Increment(ref counter) - 1}";
        return peer => map.TryGetValue(peer.CurrentPeerInfo.Id, out var k) ? k : peer.CurrentPeerInfo.Id.ToString();
    }

    [Fact]
    public async Task Wallet_Deposit_Withdraw_Transfer()
    {
        var config = Config("wallet");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var w = client.UseWallet();
        var pushes = new ConcurrentQueue<System.Collections.Generic.IReadOnlyList<CurrencyBalance>>();
        w.Changed += b => pushes.Enqueue(b);
        await client.ConnectAsync();
        await Task.Delay(150);   // let the server register the peer as online (for the push assertion)

        await wallet.DepositAsync("player0", "gold", 100);
        Assert.True(await Wait(() => !pushes.IsEmpty));
        Assert.Equal(100, (await w.GetAsync()).Single(b => b.Currency == "gold").Amount);

        Assert.True(await wallet.TryWithdrawAsync("player0", "gold", 30));
        Assert.False(await wallet.TryWithdrawAsync("player0", "gold", 1000));
        Assert.Equal(70, (await w.GetAsync()).Single(b => b.Currency == "gold").Amount);

        await wallet.DepositAsync("player1", "gold", 0);   // ensure player1 exists
        Assert.True(await wallet.TryTransferAsync("player0", "player1", "gold", 50));
        Assert.Equal(20, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);
        Assert.Equal(50, (await wallet.GetAsync("player1")).Single(b => b.Currency == "gold").Amount);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Vendor_Buy_And_Sell()
    {
        var config = Config("vendor");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        server.UseVendor(inventory, wallet).Define("shop", new[]
        {
            new VendorEntry("potion", buyPrice: 10, sellPrice: 4),
            new VendorEntry("relic", buyPrice: 100, sellPrice: 20, stock: 1),
        });
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var vendor = client.UseVendor();
        await client.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        await wallet.DepositAsync("player0", "gold", 50);

        await vendor.BuyAsync("shop", "potion", 3);   // costs 30
        Assert.Equal(20, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);
        Assert.Equal(3, (await inventory.GetAsync("player0")).Single(s => s.ItemId == "potion").Count);

        // Can't afford the relic.
        await Assert.ThrowsAsync<VendorException>(() => vendor.BuyAsync("shop", "relic", 1));

        // Sell 2 potions back at 4 each = +8.
        await vendor.SellAsync("shop", "potion", 2);
        Assert.Equal(28, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);
        Assert.Equal(1, (await inventory.GetAsync("player0")).Single(s => s.ItemId == "potion").Count);

        // Limited stock: buy the only relic (need funds), then it's out of stock.
        await wallet.DepositAsync("player0", "gold", 100);
        await vendor.BuyAsync("shop", "relic", 1);
        await Assert.ThrowsAsync<VendorException>(() => vendor.BuyAsync("shop", "relic", 1));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Crafting_Consumes_Inputs_And_Grants_Output()
    {
        var config = Config("craft");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseCrafting(inventory).Define(new Recipe("sword",
            inputs: new[] { new ItemAmount("iron", 2), new ItemAmount("wood", 1) },
            outputs: new[] { new ItemAmount("sword", 1) }));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var crafting = client.UseCrafting();
        await client.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        await inventory.GrantAsync("player0", "iron", 4);
        await inventory.GrantAsync("player0", "wood", 2);

        await crafting.CraftAsync("sword", 2);   // consumes 4 iron + 2 wood, makes 2 swords
        var inv = await inventory.GetAsync("player0");
        Assert.Equal(2, inv.Single(s => s.ItemId == "sword").Count);
        Assert.DoesNotContain(inv, s => s.ItemId == "iron");
        Assert.DoesNotContain(inv, s => s.ItemId == "wood");

        // Not enough ingredients now.
        await Assert.ThrowsAsync<CraftingException>(() => crafting.CraftAsync("sword"));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Loot_Rolls_Deterministically_And_Grants()
    {
        var config = Config("loot");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var loot = server.UseLoot(inventory, new LootOptions { Seed = 42, CanOpen = (_, __) => true });
        loot.Define(new LootTable("chest", rolls: 3, entries: new[]
        {
            new LootEntry("gold", 10, weight: 0, guaranteed: true),
            new LootEntry("common", 1, weight: 90),
            new LootEntry("rare", 1, weight: 10),
        }));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var lootClient = client.UseLoot();
        await client.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        var drops = await lootClient.OpenAsync("chest");
        Assert.Contains(drops, d => d.ItemId == "gold" && d.Count == 10);   // guaranteed always drops

        // Everything dropped was granted to the inventory.
        var inv = await inventory.GetAsync("player0");
        foreach (var d in drops)
            Assert.Equal(d.Count, inv.Single(s => s.ItemId == d.ItemId).Count);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Auction_List_Bid_And_Settle()
    {
        var config = Config("auction");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        server.UseAuction(inventory, wallet);
        _ = server.StartAsync();
        await Task.Delay(120);

        var sellerClient = new TestClient(config); var sellerAh = sellerClient.UseAuction();
        var soldSignal = new TaskCompletionSource<long>();
        sellerAh.Sold += e => soldSignal.TrySetResult(e.Amount);
        await sellerClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        var buyerClient = new TestClient(config); var buyerAh = buyerClient.UseAuction();
        var wonSignal = new TaskCompletionSource<string>();
        buyerAh.Won += e => wonSignal.TrySetResult(e.ItemId);
        await buyerClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player1") != null));

        await inventory.GrantAsync("player0", "artifact", 1);   // seller = player0
        await wallet.DepositAsync("player1", "gold", 1000);     // buyer = player1

        // List for a short duration so the timer settles it during the test.
        var listingId = await sellerAh.SellAsync("artifact", 1, minBid: 100, durationSeconds: 2);
        Assert.Empty(await inventory.GetAsync("player0"));       // item escrowed out of seller

        await buyerAh.BidAsync(listingId, 250);
        Assert.Equal(750, (await wallet.GetAsync("player1")).Single(b => b.Currency == "gold").Amount);   // bid escrowed

        // Wait for the ~1s settlement timer to fire after expiry.
        var proceeds = await AwaitOrTimeout(soldSignal.Task, 6000);
        var wonItem = await AwaitOrTimeout(wonSignal.Task, 6000);
        Assert.Equal(250, proceeds);
        Assert.Equal("artifact", wonItem);

        // Seller got paid, buyer got the item.
        Assert.Equal(250, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);
        Assert.Equal(1, (await inventory.GetAsync("player1")).Single(s => s.ItemId == "artifact").Count);

        sellerClient.Disconnect(); buyerClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Auction_Outbid_Refunds_Previous_Bidder()
    {
        var config = Config("auction-outbid");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        server.UseAuction(inventory, wallet);
        _ = server.StartAsync();
        await Task.Delay(120);

        var seller = new TestClient(config); var sellerAh = seller.UseAuction();
        await seller.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));
        var bidder1 = new TestClient(config); var ah1 = bidder1.UseAuction();
        var refunded = new TaskCompletionSource<long>();
        ah1.Outbid += e => refunded.TrySetResult(e.Amount);
        await bidder1.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player1") != null));
        var bidder2 = new TestClient(config); var ah2 = bidder2.UseAuction();
        await bidder2.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player2") != null));

        await inventory.GrantAsync("player0", "gem", 1);
        await wallet.DepositAsync("player1", "gold", 500);
        await wallet.DepositAsync("player2", "gold", 500);

        var listingId = await sellerAh.SellAsync("gem", 1, minBid: 50, durationSeconds: 30);
        await ah1.BidAsync(listingId, 100);
        Assert.Equal(400, (await wallet.GetAsync("player1")).Single(b => b.Currency == "gold").Amount);

        await ah2.BidAsync(listingId, 200);   // outbids player1
        var back = await AwaitOrTimeout(refunded.Task, 4000);
        Assert.Equal(100, back);
        Assert.Equal(500, (await wallet.GetAsync("player1")).Single(b => b.Currency == "gold").Amount);   // fully refunded
        Assert.Equal(300, (await wallet.GetAsync("player2")).Single(b => b.Currency == "gold").Amount);   // 200 escrowed

        seller.Disconnect(); bidder1.Disconnect(); bidder2.Disconnect();
        await server.StopAsync();
    }

    private static async Task<T> AwaitOrTimeout<T>(Task<T> task, int timeoutMs)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (done != task) throw new TimeoutException("Signal did not fire in time.");
        return await task;
    }

    private static async Task<bool> Wait(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}
