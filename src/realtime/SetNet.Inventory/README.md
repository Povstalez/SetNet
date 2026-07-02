<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Inventory

**Server-authoritative player inventory for [SetNet](https://www.nuget.org/packages/SetNet).**

The server owns every item. Game logic grants and revokes stackable items **by player key** (online or not); connected clients can read their inventory and subscribe to changes but never mutate it directly. The atomic take-if-enough primitive (`TryRevokeAsync`) is what makes safe trading and mail attachments possible — [`SetNet.Trade`](https://www.nuget.org/packages/SetNet.Trade) and [`SetNet.Mail`](https://www.nuget.org/packages/SetNet.Mail) move items through this same hub. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
```

## Usage

Call `InventoryRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — the authority:

```csharp
InventoryRuntime.Enable();
var inventory = server.UseInventory();   // + optional IInventoryStore / InventoryOptions

// from game logic (a quest reward, a purchase, a drop):
await inventory.GrantAsync(playerKey, "gold", 100);
bool paid = await inventory.TryRevokeAsync(playerKey, "gold", 100);   // false if they can't afford it
```

**Client** — read and react:

```csharp
InventoryRuntime.Enable();
var inventory = client.UseInventory();
inventory.Changed += stacks => Redraw(stacks);

foreach (var s in await inventory.GetAsync())
    Console.WriteLine($"{s.Count} × {s.ItemId}");
```

## API

**Server:** `server.UseInventory(IInventoryStore?, InventoryOptions?)` → `InventoryServer`

| Member | Purpose |
|---|---|
| `GrantAsync(playerKey, itemId, count)` | add items (pushes a snapshot if the player is online) |
| `Task<bool> TryRevokeAsync(playerKey, itemId, count)` | atomic remove; false if insufficient |
| `GetAsync(playerKey)` | current stacks |
| `KeyOf(peer)` / `PeerFor(playerKey)` / `PushAsync(playerKey)` | identity + online helpers (used by trade/mail) |
| `Store` | the backing `IInventoryStore` |

**Client:** `var inventory = client.UseInventory()` → `InventoryClient` — `GetAsync()`, `event Changed`.

**Options:** `InventoryOptions.PlayerKey` maps a peer to its stable key (default = connection id — override to the authenticated account id so inventories survive reconnects).

`InventoryRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65487 / 65488 / 65489.** Don't reuse them.
- **Stackable by item id.** A stack is `(ItemId, Count)`. Per-instance data (unique weapons, durability) is out of scope — encode it into the item id (`"sword#<uuid>"`) so each instance is its own non-stacking stack.
- **Use a stable player key.** The default key is the connection id, which changes on reconnect. With [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth), set `PlayerKey` to the authenticated account id so inventories persist and follow the player across nodes.
- **Persistence.** The default `MemoryInventoryStore` is per-process. Implement `IInventoryStore` over Redis/SQL for durability and cluster sharing — keep `TryRevokeAsync` atomic (it's the anti-dupe guarantee).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
