using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using SetNet.Inventory;
using SetNet.Mail;
using SetNet.Trade;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for the player-data trio (Inventory / Trade / Mail). They share a stable player-key resolver
/// so two connected test clients can address each other by account, not by transient connection id.
/// </summary>
[Collection("integration")]
public class InventoryTradeMailTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    // A stable per-peer key so trades/mail can target players across the two test clients. In a real app this
    // comes from SetNet.Auth; here we tag each connection deterministically by connect order.
    /// <summary>Assigns "player0", "player1", … to peers in the order they connect (deterministic for tests).</summary>
    private static Func<BasePeer, string> OrderedKeys(BaseServer server)
    {
        var map = new ConcurrentDictionary<Guid, string>();
        var counter = 0;
        server.PeerConnected += peer => map[peer.CurrentPeerInfo.Id] = $"player{System.Threading.Interlocked.Increment(ref counter) - 1}";
        return peer => map.TryGetValue(peer.CurrentPeerInfo.Id, out var k) ? k : peer.CurrentPeerInfo.Id.ToString();
    }

    [Fact]
    public async Task Grant_Revoke_And_Push()
    {
        var config = Config("inv");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(config);
        var inv = client.UseInventory();
        var pushes = new ConcurrentQueue<System.Collections.Generic.IReadOnlyList<ItemStack>>();
        inv.Changed += stacks => pushes.Enqueue(stacks);
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player0") != null));

        await inventory.GrantAsync("player0", "gold", 100);
        Assert.True(await WaitUntil(() => !pushes.IsEmpty));   // grant pushed a snapshot to the online player

        var stacks = await inv.GetAsync();
        Assert.Contains(stacks, s => s.ItemId == "gold" && s.Count == 100);

        Assert.True(await inventory.TryRevokeAsync("player0", "gold", 40));
        Assert.False(await inventory.TryRevokeAsync("player0", "gold", 1000));   // can't overdraw
        var after = await inv.GetAsync();
        Assert.Equal(60, after.Single(s => s.ItemId == "gold").Count);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Trade_Swaps_Items_Atomically()
    {
        var config = Config("trade");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseTrade(inventory);
        _ = server.StartAsync();
        await Task.Delay(120);

        // Two clients co-located in one test process: pass each its own key so perspective-rendered trade
        // events route to the right client (connect order fixes player0/player1 via OrderedKeys).
        var aClient = new TestClient(config);
        var aTrade = aClient.UseTrade("player0");
        await aClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player0") != null));

        var bClient = new TestClient(config);
        var bTrade = bClient.UseTrade("player1");
        TradeView? bLast = null;
        var bRequested = false;
        bTrade.TradeRequested += (_, v) => bRequested = true;
        bTrade.Updated += v => bLast = v;
        var aDone = new TaskCompletionSource<bool>();
        var bDone = new TaskCompletionSource<bool>();
        aTrade.Completed += _ => aDone.TrySetResult(true);
        bTrade.Completed += _ => bDone.TrySetResult(true);
        await bClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player1") != null));

        // Seed inventories: player0 has a sword, player1 has gold.
        await inventory.GrantAsync("player0", "sword", 1);
        await inventory.GrantAsync("player1", "gold", 500);

        await aTrade.ProposeAsync("player1");
        Assert.True(await WaitUntil(() => bRequested));

        await aTrade.OfferAsync("sword", 1);
        await bTrade.OfferAsync("gold", 500);
        Assert.True(await WaitUntil(() => bLast != null && bLast.PartnerOffer.Any(s => s.ItemId == "sword")));

        await aTrade.SetReadyAsync(true);
        await bTrade.SetReadyAsync(true);
        Assert.True(await WaitUntil(() => bLast != null && bLast.State == TradeState.Confirming));

        await aTrade.ConfirmAsync();
        await bTrade.ConfirmAsync();

        Assert.True(await WaitUntil(() => aDone.Task.IsCompleted && bDone.Task.IsCompleted));

        // Items crossed over; escrow left neither side holding both.
        var a = await inventory.GetAsync("player0");
        var b = await inventory.GetAsync("player1");
        Assert.Contains(a, s => s.ItemId == "gold" && s.Count == 500);
        Assert.DoesNotContain(a, s => s.ItemId == "sword");
        Assert.Contains(b, s => s.ItemId == "sword" && s.Count == 1);
        Assert.DoesNotContain(b, s => s.ItemId == "gold");

        aClient.Disconnect(); bClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Editing_An_Offer_Resets_Ready()
    {
        var config = Config("trade-reset");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseTrade(inventory);
        _ = server.StartAsync();
        await Task.Delay(120);

        var aClient = new TestClient(config); var aTrade = aClient.UseTrade("player0");
        await aClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player0") != null));
        var bClient = new TestClient(config); var bTrade = bClient.UseTrade("player1");
        TradeView? aLast = null;
        aTrade.Updated += v => aLast = v;
        await bClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player1") != null));

        await inventory.GrantAsync("player0", "gem", 3);
        await aTrade.ProposeAsync("player1");
        await Task.Delay(100);
        await aTrade.OfferAsync("gem", 1);
        await aTrade.SetReadyAsync(true);
        Assert.True(await WaitUntil(() => aLast != null && aLast.YouReady));

        // Changing the offer after readying must clear ready (partner re-approves the exact final offer).
        await aTrade.OfferAsync("gem", 2);
        Assert.True(await WaitUntil(() => aLast != null && !aLast.YouReady && aLast.State == TradeState.Open));

        aClient.Disconnect(); bClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Mail_Escrows_And_Delivers_Attachments()
    {
        var config = Config("mail");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseMail(inventory: inventory, options: new MailOptions { PlayerKey = keys });
        _ = server.StartAsync();
        await Task.Delay(120);

        var senderClient = new TestClient(config); var senderMail = senderClient.UseMail();
        await senderClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player0") != null));
        var recipientClient = new TestClient(config); var recipientMail = recipientClient.UseMail();
        var received = new ConcurrentQueue<MailMessage>();
        recipientMail.Received += m => received.Enqueue(m);
        await recipientClient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player1") != null));

        await inventory.GrantAsync("player0", "potion", 5);

        // Send 3 potions; they're escrowed out of the sender immediately.
        var mailId = await senderMail.SendAsync("player1", "Health kit", null, new[] { new MailAttachment("potion", 3) });
        Assert.Equal(2, (await inventory.GetAsync("player0")).Single(s => s.ItemId == "potion").Count);
        Assert.True(await WaitUntil(() => !received.IsEmpty));   // pushed to the online recipient

        // Can't attach items you don't have — escrow rejects it.
        await Assert.ThrowsAsync<MailException>(() => senderMail.SendAsync("player1", "nope", null, new[] { new MailAttachment("potion", 999) }));

        // Recipient claims → attachments land in their inventory.
        await recipientMail.ClaimAsync(mailId);
        Assert.Equal(3, (await inventory.GetAsync("player1")).Single(s => s.ItemId == "potion").Count);

        var box = await recipientMail.ListAsync();
        Assert.Contains(box, m => m.Id == mailId && m.Claimed);

        senderClient.Disconnect(); recipientClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Deleting_Unclaimed_Mail_Returns_Items_To_Sender()
    {
        var config = Config("mail-del");
        var server = new TestServer(config);
        var keys = OrderedKeys(server);
        var inventory = server.UseInventory(options: new InventoryOptions { PlayerKey = keys });
        server.UseMail(inventory: inventory, options: new MailOptions { PlayerKey = keys });
        _ = server.StartAsync();
        await Task.Delay(120);

        var sender = new TestClient(config); var senderMail = sender.UseMail();
        await sender.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player0") != null));
        var recipient = new TestClient(config); var recipientMail = recipient.UseMail();
        await recipient.ConnectAsync();
        Assert.True(await WaitUntil(() => inventory.PeerFor("player1") != null));

        await inventory.GrantAsync("player0", "ore", 10);
        var id = await senderMail.SendAsync("player1", "ore delivery", null, new[] { new MailAttachment("ore", 10) });
        Assert.Empty(await inventory.GetAsync("player0"));   // all escrowed

        await recipientMail.DeleteAsync(id);   // recipient discards without claiming
        Assert.True(await WaitUntil(async () => (await inventory.GetAsync("player0")).Any(s => s.ItemId == "ore" && s.Count == 10)));

        sender.Disconnect(); recipient.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }

    private static async Task<bool> WaitUntil(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (await condition()) return true; await Task.Delay(20); }
        return await condition();
    }
}
