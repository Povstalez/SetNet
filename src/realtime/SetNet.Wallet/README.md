<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Wallet

**Server-authoritative player currencies for [SetNet](https://www.nuget.org/packages/SetNet).**

The server owns every coin. Game logic deposits, withdraws, and transfers named currencies **by player key** with atomic anti-overdraft guarantees; clients read their balances and subscribe to changes but never mutate them. It's the money side of the economy — [`SetNet.Vendor`](https://www.nuget.org/packages/SetNet.Vendor) and [`SetNet.Auction`](https://www.nuget.org/packages/SetNet.Auction) move currency through this same hub. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Wallet
```

## Usage

Call `WalletRuntime.Enable()` once at startup on both ends.

```csharp
// server
WalletRuntime.Enable();
var wallet = server.UseWallet();
await wallet.DepositAsync(playerKey, "gold", 500);
bool paid = await wallet.TryWithdrawAsync(playerKey, "gold", 100);        // false if broke
bool sent = await wallet.TryTransferAsync(fromKey, toKey, "gold", 50);    // atomic

// client
WalletRuntime.Enable();
var wallet = client.UseWallet();
wallet.Changed += balances => Redraw(balances);
foreach (var b in await wallet.GetAsync()) Console.WriteLine($"{b.Amount} {b.Currency}");
```

## API

**Server:** `server.UseWallet(IWalletStore?, WalletOptions?)` → `WalletServer` — `DepositAsync`, `TryWithdrawAsync`, `TryTransferAsync`, `GetAsync`, `KeyOf(peer)`, `PushAsync`.
**Client:** `client.UseWallet()` → `WalletClient` — `GetAsync()`, `event Changed`.
**Store:** `IWalletStore` (`MemoryWalletStore` default). **Options:** `WalletOptions.PlayerKey`.

`WalletRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Wallet` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Atomic by contract.** `TryWithdrawAsync`/`TryTransferAsync` never overdraw and never dupe. Back the store with a transactional store (Redis/SQL) for a multi-node cluster.
- **Stable player key.** Default is the connection id; with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) set `WalletOptions.PlayerKey` to the account id so wallets persist.
- Mirrors [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory): items there, money here.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
