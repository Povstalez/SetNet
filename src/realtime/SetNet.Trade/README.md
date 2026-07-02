<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Trade

**Player-to-player item trading for [SetNet](https://www.nuget.org/packages/SetNet) — server-authoritative, scam-proof.**

Two players put items on the table, both mark **ready**, and then both must **confirm** — the second phase locks the offers so nobody can swap in a worse deal at the last instant. When both confirm, the server performs an **atomic cross-swap** through [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory): each side's items are revoked and granted to the other, and if anyone's holdings changed in between, the whole thing rolls back and cancels. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Trade
```

## Usage

Call `InventoryRuntime.Enable()` and `TradeRuntime.Enable()` once at startup on both ends.

**Server** — pass the same inventory hub:

```csharp
InventoryRuntime.Enable(); TradeRuntime.Enable();
var inventory = server.UseInventory();
server.UseTrade(inventory);
```

**Client** — propose, offer, confirm:

```csharp
InventoryRuntime.Enable(); TradeRuntime.Enable();
var trade = client.UseTrade();

trade.TradeRequested += (fromKey, view) => ShowTradeWindow(fromKey);
trade.Updated        += view => Redraw(view);          // offers/ready/confirm changed
trade.Completed      += view => Toast("Trade complete");
trade.Cancelled      += reason => Toast($"Trade cancelled: {reason}");

await trade.ProposeAsync(otherPlayerKey);
await trade.OfferAsync("gold", 500);
await trade.OfferAsync("sword#42", 1);
await trade.SetReadyAsync(true);        // when both ready → confirming
await trade.ConfirmAsync();             // when both confirm → items swap
```

## API

**Server:** `server.UseTrade(InventoryServer inventory)` → `TradeServer` (state machine + atomic swap).

**Client:** `var trade = client.UseTrade()` → `TradeClient`

| Member | Purpose |
|---|---|
| `Task<string> ProposeAsync(targetPlayerKey)` | start a trade, returns the trade id |
| `Task OfferAsync(itemId, count)` | set/remove an offered item (count 0 removes); resets ready/confirm |
| `Task SetReadyAsync(bool)` | toggle ready; both ready ⇒ confirming |
| `Task ConfirmAsync()` | confirm; both confirm ⇒ swap |
| `Task CancelAsync()` | cancel (tolerant of disconnect) |
| `event Action<string, TradeView> TradeRequested` | someone proposed to you |
| `event Action<TradeView> Updated` / `Completed`; `event Action<string> Cancelled` | lifecycle |
| `string? TradeId` | the current trade, or null |

`InventoryRuntime.Enable()` + `TradeRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Trade` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Two-phase by design.** Editing any offer clears both ready and confirm flags, so a partner always re-approves the exact final offer. In the confirming phase offers are locked — to change something, cancel and restart.
- **Atomicity.** The swap revokes everything first (via `Inventory.TryRevokeAsync`) and only then grants; a shortfall triggers a full rollback + cancel. For a multi-node cluster, back `SetNet.Inventory` with a transactional store so the revoke/grant sequence is durable.
- **One trade per player.** A player already in a trade can't be proposed to; a disconnect auto-cancels their trade with nothing moved.
- **Identity.** Trades address players by the inventory hub's player key — use a stable key (see [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) notes) and gate access with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
