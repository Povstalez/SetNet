using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using SetNet.Inventory;
using SetNet.Marketplace;
using SetNet.StatusEffects;
using SetNet.Wallet;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for the marketplace order book and status effects.</summary>
[Collection("integration")]
public class MarketplaceStatusTests
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
    public async Task Crossing_Orders_Match_At_Resting_Price_With_Refund()
    {
        var config = Config("market");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        server.UseMarketplace(inventory, wallet);
        _ = server.StartAsync();
        await Task.Delay(120);

        var sellerClient = new TestClient(config); var sellerMarket = sellerClient.UseMarketplace();
        var sellerFills = new ConcurrentQueue<MarketFill>();
        sellerMarket.Filled += f => sellerFills.Enqueue(f);
        await sellerClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        var buyerClient = new TestClient(config); var buyerMarket = buyerClient.UseMarketplace();
        var buyerFills = new ConcurrentQueue<MarketFill>();
        buyerMarket.Filled += f => buyerFills.Enqueue(f);
        await buyerClient.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player1") != null));

        await inventory.GrantAsync("player0", "ore", 100);   // seller
        await wallet.DepositAsync("player1", "gold", 1000);  // buyer

        // Seller rests an ask of 100 ore @ 5.
        await sellerMarket.PostSellAsync("ore", 100, price: 5);
        Assert.Empty(await inventory.GetAsync("player0"));    // items escrowed

        // Buyer bids 40 @ 6 → crosses, trades at the resting price 5, and is refunded 1/unit.
        await buyerMarket.PostBuyAsync("ore", 40, price: 6);

        Assert.True(await Wait(() => !buyerFills.IsEmpty && !sellerFills.IsEmpty));
        Assert.Contains(buyerFills, f => f.Side == OrderSide.Buy && f.Quantity == 40 && f.Price == 5);
        Assert.Contains(sellerFills, f => f.Side == OrderSide.Sell && f.Quantity == 40 && f.Price == 5);

        // Buyer paid 40×5 = 200 (escrowed 40×6 = 240, refunded 40); has 40 ore.
        Assert.Equal(800, (await wallet.GetAsync("player1")).Single(b => b.Currency == "gold").Amount);
        Assert.Equal(40, (await inventory.GetAsync("player1")).Single(s => s.ItemId == "ore").Count);
        // Seller received 200 gold; 60 ore still escrowed on the resting ask.
        Assert.Equal(200, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);

        // The book shows the remaining 60 @ 5 on the sell side.
        var book = await buyerMarket.GetBookAsync("ore");
        Assert.Equal(60, book.Sells.Single(l => l.Price == 5).Quantity);
        Assert.Empty(book.Buys);   // buy fully filled

        sellerClient.Disconnect(); buyerClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Cancel_Returns_Escrow()
    {
        var config = Config("market-cancel");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        var wallet = server.UseWallet(options: new WalletOptions { PlayerKey = keys });
        server.UseMarketplace(inventory, wallet);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config); var market = client.UseMarketplace();
        await client.ConnectAsync();
        Assert.True(await Wait(() => inventory.PeerFor("player0") != null));

        await wallet.DepositAsync("player0", "gold", 500);
        var orderId = await market.PostBuyAsync("sword", 5, price: 50);   // escrows 250
        Assert.Equal(250, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);

        var mine = await market.MyOrdersAsync();
        Assert.Single(mine);

        await market.CancelAsync(orderId);
        Assert.Equal(500, (await wallet.GetAsync("player0")).Single(b => b.Currency == "gold").Amount);   // refunded
        Assert.Empty(await market.MyOrdersAsync());

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task StatusEffect_Applies_Expires_And_Pushes_To_Watcher()
    {
        var config = Config("status");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var effects = server.UseStatusEffects(new StatusEffectOptions { TargetKey = keys, TickIntervalMs = 100 });
        effects.Define(new StatusEffectDefinition("poison", maxStacks: 5, defaultDurationMs: 500, stacking: StackPolicy.Stack, isDebuff: true));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config); var status = client.UseStatusEffects();
        var updates = new ConcurrentQueue<(string target, System.Collections.Generic.IReadOnlyList<StatusEffect> effects)>();
        status.Changed += (t, list) => updates.Enqueue((t, list));
        await client.ConnectAsync();
        await Task.Delay(120);   // let the server register the peer

        // Watch a mob entity (no peer of its own); this client will receive its effect updates.
        await status.WatchAsync("mob:dragon");

        await effects.ApplyAsync("mob:dragon", "poison", stacks: 2, magnitude: 10, source: "player0");
        Assert.True(await Wait(() => updates.Any(u => u.target == "mob:dragon" && u.effects.Any(e => e.EffectId == "poison" && e.Stacks == 2))));

        // Stacking policy adds stacks up to the cap.
        await effects.ApplyAsync("mob:dragon", "poison", stacks: 2);
        Assert.True(await Wait(() => updates.Any(u => u.effects.Any(e => e.EffectId == "poison" && e.Stacks == 4))));

        var snapshot = effects.GetAsync("mob:dragon");
        Assert.Equal(4, snapshot.Single(e => e.EffectId == "poison").Stacks);

        // After the duration lapses, the timer expires it and pushes an empty list.
        Assert.True(await Wait(() => updates.Any(u => u.target == "mob:dragon" && !u.effects.Any()), timeoutMs: 3000));
        Assert.Empty(effects.GetAsync("mob:dragon"));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task StatusEffect_Remove_And_Refresh()
    {
        var config = Config("status2");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var effects = server.UseStatusEffects(new StatusEffectOptions { TargetKey = keys, TickIntervalMs = 100 });
        effects.Define(new StatusEffectDefinition("haste", defaultDurationMs: 10000, stacking: StackPolicy.Refresh));
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config); var status = client.UseStatusEffects();
        StatusEffect? last = null;
        status.Changed += (t, list) => last = list.FirstOrDefault(e => e.EffectId == "haste");
        await client.ConnectAsync();
        await Task.Delay(120);

        // The affected player is pushed their own effects automatically (no explicit watch).
        await effects.ApplyAsync("player0", "haste", magnitude: 1.5);
        Assert.True(await Wait(() => last != null && last.Magnitude == 1.5));

        await effects.RemoveAsync("player0", "haste");
        Assert.True(await Wait(() => !effects.GetAsync("player0").Any()));

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> Wait(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}
