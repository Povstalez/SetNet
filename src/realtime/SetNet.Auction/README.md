<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Auction

**Player-driven auction house for [SetNet](https://www.nuget.org/packages/SetNet).**

List an item (it's **escrowed** from your inventory), let players bid or buy out (each bid **escrows** the bidder's currency and refunds the previous high bidder), and a background timer settles expired listings — the winner gets the item, the seller gets the winning bid, or the item comes back if nobody bid. Every move goes through [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) and [`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet), so escrow can't dupe or lose anything. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Wallet
dotnet add package SetNet.Auction
```

## Usage

Call `InventoryRuntime.Enable()`, `WalletRuntime.Enable()`, and `AuctionRuntime.Enable()` at startup.

```csharp
// server
InventoryRuntime.Enable(); WalletRuntime.Enable(); AuctionRuntime.Enable();
var inventory = server.UseInventory();
var wallet = server.UseWallet();
server.UseAuction(inventory, wallet);

// client
InventoryRuntime.Enable(); WalletRuntime.Enable(); AuctionRuntime.Enable();
var ah = client.UseAuction();
ah.Outbid   += e => Toast($"Outbid — {e.Amount} {e.Currency} refunded");
ah.Won      += e => Toast($"Won {e.Count}× {e.ItemId}!");
ah.Sold     += e => Toast($"Sold for {e.Amount} {e.Currency}");
ah.Returned += e => Toast("Listing expired — item returned");

string id = await ah.SellAsync("epic_sword", 1, minBid: 500, durationSeconds: 3600, buyout: 5000);
foreach (var l in await ah.BrowseAsync()) ShowRow(l);
await ah.BidAsync(someListingId, 600);
await ah.BuyoutAsync(someListingId);
```

## API

**Server:** `server.UseAuction(InventoryServer, WalletServer)` → `AuctionServer` (holds listings, runs the settlement timer; `Dispose()` stops it).
**Client:** `client.UseAuction()` → `AuctionClient`

| Member | Purpose |
|---|---|
| `BrowseAsync()` | active listings |
| `SellAsync(itemId, count, minBid, durationSeconds, buyout = 0, currency = "gold")` | list (escrows the item) |
| `BidAsync(listingId, amount)` / `BuyoutAsync(listingId)` | bid / instant-buy (escrows currency) |
| `CancelAsync(listingId)` | cancel your bid-free listing |
| `event Outbid` / `Won` / `Sold` / `Returned` | outcomes |

`InventoryRuntime.Enable()` + `WalletRuntime.Enable()` + `AuctionRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65470 / 65471 / 65472.** Don't reuse them.
- **Escrow everywhere.** The item leaves the seller's inventory at listing; each bid withdraws currency and refunds the prior bidder; settlement grants item↔currency. Nothing is created or destroyed — a cancel/expiry returns the item, an outbid returns the money.
- **Settlement** runs on a ~1 s timer; a buyout settles instantly. Cancel is only allowed before the first bid.
- **Persistence.** Listings and escrow live in memory here; back `SetNet.Inventory`/`SetNet.Wallet` with durable stores and persist listings yourself if auctions must survive a restart.
- **Node-local.** Listings live on the auction node. For a cluster, run one auction endpoint (or shard sellers/buyers to it) so escrow stays consistent.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
