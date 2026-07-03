<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Equipment

**Server-authoritative equipment for [SetNet](https://www.nuget.org/packages/SetNet).**

Define your own slot layout — head, weapon, `ring1`/`ring2`, whatever — as a custom `EquipmentSchema` with per-slot accept rules, then equip and unequip items pulled straight from [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory). Each equipped item applies its [`SetNet.Stats`](https://www.nuget.org/packages/SetNet.Stats) modifiers to the wearer's `StatSet` (and removes them on unequip), so gear changes attack power, defense, speed, and so on. Clients equip/unequip and read the loadout; changes push. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Equipment
```

## Usage

Call `EquipmentRuntime.Enable()` once at startup on both ends.

**Server** — over the inventory hub, with your slot schema and item-stat mapping:

```csharp
EquipmentRuntime.Enable();
var inventory = server.UseInventory();

var schema = EquipmentSchema.Create()
    .Slot("weapon", accepts: id => id.StartsWith("weapon:"))
    .Slot("head")
    .Slot("ring1").Slot("ring2")
    .Build();

var equipment = server.UseEquipment(inventory, new EquipmentOptions
{
    Schema    = schema,
    ItemStats = itemId => Catalog.ModifiersFor(itemId),   // item id -> stat modifiers while worn
    StatsOf   = playerKey => world.StatsOf(playerKey),    // must be the wearer's StatSet
    // PlayerKey must match the Inventory hub's key mapping
});

// server logic can equip directly, too:
var (ok, msg) = await equipment.EquipAsync(playerKey, "weapon", "weapon:sword");
```

**Client** — equip, unequip, and read the loadout:

```csharp
EquipmentRuntime.Enable();
var equipment = client.UseEquipment();
equipment.Changed += loadout => Redraw(loadout);   // slot -> item id

await equipment.EquipAsync("weapon", "weapon:sword");
await equipment.UnequipAsync("head");
var loadout = await equipment.GetAsync();
```

## Notes

- **Custom slots with accept rules.** `EquipmentSchema.Create().Slot(id, accepts?)` — the optional predicate gates which item ids a slot takes; unknown slots and rejected items fail with a reason.
- **Items live in exactly one place.** `EquipAsync` atomically takes the item from `InventoryServer` (via `TryRevokeAsync`), swaps out and returns whatever was already in the slot, and updates stats; `UnequipAsync` grants the item back. No dupes.
- **Gear = stat modifiers.** `EquipmentOptions.ItemStats` maps an item id to the `StatModifier`s it grants; they're re-tagged per slot so `RemoveBySource` strips exactly that slot's bonuses on unequip or swap. Provide `StatsOf` to reach the wearer's `StatSet` (omit it for slots-only equipment with no stat effect).
- **Key parity.** `EquipmentOptions.PlayerKey` must map a peer to the **same** stable key the Inventory hub uses, or items won't line up. Default is the connection id — override to the authenticated account id.
- **Pluggable store.** `IEquipmentStore` holds each player's loadout (`MemoryEquipmentStore` default, in-process); implement it over Redis/SQL for durability.
- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Equipment` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. The control protocol is hand-framed `byte[]`.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
