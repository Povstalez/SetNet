# SetNet.Inventory.Grid

A **spatial ("tetris") inventory** for SetNet — Diablo/Tarkov style. Items occupy a **width×height footprint**, can be **rotated 90°**, and are placed on a 2D grid with **server-authoritative occupancy checks**. Pure data structure, **zero dependencies**.

```csharp
using SetNet.Inventory.Grid;

var backpack = new GridInventory(width: 10, height: 6);

var rifle = new GridItem { Type = "ak74", Width = 5, Height = 2, Tag = weaponData };
backpack.TryPlaceAt(rifle, new GridPos(0, 0));           // place at a cell
backpack.TryRotate(rifle.Id);                            // rotate 90° in place (if it fits)
backpack.TryMove(rifle.Id, new GridPos(2, 3), GridRotation.None);

var potion = new GridItem { Type = "medkit", Width = 1, Height = 2 };
backpack.TryAdd(potion);                                 // auto-fit at the first free spot (tries both rotations)

var here = backpack.At(new GridPos(2, 3));               // what's under a cell
backpack.Remove(rifle.Id);
```

- **Footprint + rotation** — `Width`×`Height`; `GridRotation.Quarter` swaps them. `PlacedItem.Width/Height` give the effective size.
- **Occupancy** — `CanPlace(w, h, pos, ignoreId?)`, `IsFree(pos)`, `At(pos)`, `FreeCells`.
- **Placement** — `TryPlaceAt(item, pos, rotation)`, `TryAdd(item)` (auto-fit), `TryMove`, `TryRotate`, `Remove`.
- **Stacking** — set `MaxStack > 1`; `TryAdd` merges into existing stacks of the same `Type` first, then places the remainder — **all-or-nothing** (rolls back if it can't fully fit). `Changed` event on every mutation.

Items carry a `Tag` (your durability/mods/custom data), so nothing is lost when you persist the grid. Complements the quantity-based [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) (use that for a simple stackable bag; use this when placement/space matters).

No SetNet dependency — plain C#. **License:** MIT.
