<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Marketplace

**A continuous order-book marketplace for [SetNet](https://www.nuget.org/packages/SetNet) — like a stock exchange for your game economy.**

Players post **limit buy and sell orders**; the server runs a double-sided order book per item with **price-time priority**. Crossing orders match *instantly* at the resting order's price and the remainder rests on the book — no timers, no per-item auctions. Items and currency are escrowed on post and moved through [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) / [`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet), and a marketable order can only improve on its limit (the buyer is refunded the difference). Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Wallet
dotnet add package SetNet.Marketplace
```

## Usage

Call `InventoryRuntime.Enable()`, `WalletRuntime.Enable()`, and `MarketplaceRuntime.Enable()` at startup.

```csharp
// server
InventoryRuntime.Enable(); WalletRuntime.Enable(); MarketplaceRuntime.Enable();
var inventory = server.UseInventory();
var wallet = server.UseWallet();
server.UseMarketplace(inventory, wallet);

// client
InventoryRuntime.Enable(); WalletRuntime.Enable(); MarketplaceRuntime.Enable();
var market = client.UseMarketplace();
market.Filled += f => Toast($"{f.Side} {f.Quantity}× {f.ItemId} @ {f.Price}");

await market.PostSellAsync("iron_ore", quantity: 100, price: 5);   // ask
await market.PostBuyAsync ("iron_ore", quantity: 40,  price: 6);   // bid — crosses, trades at 5, 5 refunded/unit

MarketBook book = await market.GetBookAsync("iron_ore");           // aggregated levels
foreach (var o in await market.MyOrdersAsync()) Show(o);
await market.CancelAsync(orderId);
```

## API

**Server:** `server.UseMarketplace(InventoryServer, WalletServer)` → `MarketplaceServer`.
**Client:** `client.UseMarketplace()` → `MarketplaceClient`

| Member | Purpose |
|---|---|
| `Task<string> PostBuyAsync(itemId, quantity, price, currency = "gold")` | bid (escrows currency) |
| `Task<string> PostSellAsync(itemId, quantity, price, currency = "gold")` | ask (escrows items) |
| `Task CancelAsync(orderId)` | cancel + return remaining escrow |
| `Task<MarketBook> GetBookAsync(itemId, currency = "gold")` | aggregated bids/asks |
| `Task<IReadOnlyList<MarketOrder>> MyOrdersAsync()` | your open orders |
| `event Action<MarketFill> Filled` | one of your orders (partially) filled |

`InventoryRuntime.Enable()` + `WalletRuntime.Enable()` + `MarketplaceRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65451 / 65452 / 65453.** Don't reuse them.
- **Price-time priority.** Best price first, ties broken by who posted earlier. A trade executes at the **resting** order's price, so an incoming order never does worse than its limit; a buyer whose limit beats the ask is refunded the difference per unit.
- **Escrow on post.** A buy escrows `price × quantity` currency; a sell escrows `quantity` items. Matching is decided under a per-book lock; the item/currency moves run afterward through `SetNet.Inventory`/`SetNet.Wallet` — nothing is created or lost, and a cancel returns the remaining escrow.
- **Marketplace vs [Auction](https://www.nuget.org/packages/SetNet.Auction) vs [Trade](https://www.nuget.org/packages/SetNet.Trade):** Marketplace is a continuous many-to-many exchange (fungible goods, instant matching); Auction is timed bidding on one specific listing; Trade is a direct two-party swap. Use Marketplace for commodity economies.
- **Node-local books.** The book lives on the marketplace node; run one endpoint (or shard by item) so escrow stays consistent. Back `SetNet.Inventory`/`SetNet.Wallet` with durable stores for persistence.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
