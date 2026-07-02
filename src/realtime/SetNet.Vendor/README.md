<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Vendor

**NPC shops for [SetNet](https://www.nuget.org/packages/SetNet).**

Define vendor catalogs — buy price, sell-back price, currency, and stock per item. Clients browse and buy/sell, and the server settles each transaction **atomically** through [`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet) and [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory): a buy reserves stock, withdraws currency, then grants the item; a sell revokes the item, then pays out. Money moves before goods (and vice-versa), so a failure never conjures either. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Wallet
dotnet add package SetNet.Vendor
```

## Usage

Call `InventoryRuntime.Enable()`, `WalletRuntime.Enable()`, and `VendorRuntime.Enable()` at startup.

```csharp
// server
InventoryRuntime.Enable(); WalletRuntime.Enable(); VendorRuntime.Enable();
var inventory = server.UseInventory();
var wallet = server.UseWallet();
server.UseVendor(inventory, wallet).Define("blacksmith", new[]
{
    new VendorEntry("iron_sword", buyPrice: 120, sellPrice: 40),              // unlimited stock
    new VendorEntry("rare_shield", buyPrice: 800, sellPrice: 200, stock: 3),  // limited
});

// client
InventoryRuntime.Enable(); WalletRuntime.Enable(); VendorRuntime.Enable();
var vendor = client.UseVendor();
foreach (var e in await vendor.ListAsync("blacksmith")) ShowRow(e);
await vendor.BuyAsync("blacksmith", "iron_sword");     // throws VendorException if broke/out of stock
await vendor.SellAsync("blacksmith", "old_dagger", 2);
```

## API

**Server:** `server.UseVendor(InventoryServer, WalletServer)` → `VendorServer` — `Define(vendorId, entries)`.
**Client:** `client.UseVendor()` → `VendorClient` — `ListAsync(vendorId)`, `BuyAsync(vendorId, itemId, count)`, `SellAsync(vendorId, itemId, count)`.

`InventoryRuntime.Enable()` + `WalletRuntime.Enable()` + `VendorRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Vendor` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Atomic settlement.** Buy: reserve stock → `Wallet.TryWithdraw` → `Inventory.Grant` (stock refunded if the charge fails). Sell: `Inventory.TryRevoke` → `Wallet.Deposit`. No dupes, no free items.
- **Prices & stock are server-owned.** Clients can only transact against the catalog you `Define`. `sellPrice: 0` means the vendor won't buy; `stock: -1` is unlimited (sells don't restock).
- Resulting wallet/inventory changes reach clients via their [`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet) / [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) `Changed` subscriptions.
- For **player-to-player** sales use [`SetNet.Auction`](https://www.nuget.org/packages/SetNet.Auction) or [`SetNet.Trade`](https://www.nuget.org/packages/SetNet.Trade); Vendor is player-to-NPC.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
