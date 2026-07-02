<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Crafting

**Server-authoritative crafting for [SetNet](https://www.nuget.org/packages/SetNet).**

Register recipes as *inputs → outputs*. A client requests a craft and the server validates and **atomically** consumes the ingredients from the player's inventory (rolling back if anything's missing) before granting the results — through the shared [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) hub, so there's no way to craft something for free. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Crafting
```

## Usage

Call `InventoryRuntime.Enable()` and `CraftingRuntime.Enable()` once at startup.

```csharp
// server
InventoryRuntime.Enable(); CraftingRuntime.Enable();
var inventory = server.UseInventory();
var crafting = server.UseCrafting(inventory)
    .Define(new Recipe("iron_sword",
        inputs:  new[] { new ItemAmount("iron", 3), new ItemAmount("wood", 1) },
        outputs: new[] { new ItemAmount("iron_sword", 1) }));

// client
InventoryRuntime.Enable(); CraftingRuntime.Enable();
var crafting = client.UseCrafting();
foreach (var r in await crafting.ListAsync()) ShowRecipe(r);
await crafting.CraftAsync("iron_sword");        // throws CraftingException if ingredients missing
```

## API

**Server:** `server.UseCrafting(InventoryServer)` → `CraftingServer` — `Define(Recipe)`, `CraftAsync(playerKey, recipeId, times)`.
**Client:** `client.UseCrafting()` → `CraftingClient` — `CraftAsync(recipeId, times = 1)`, `ListAsync()`.

`InventoryRuntime.Enable()` + `CraftingRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65467 / 65468.** Don't reuse them.
- **Atomic.** Inputs are revoked before outputs are granted; a shortfall rolls back everything taken.
- **Server owns recipes.** Clients can only craft what the server registered; `ListAsync` just mirrors the book for UI.
- Resulting inventory changes arrive via the client's [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) `Changed` subscription.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
