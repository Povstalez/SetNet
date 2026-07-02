<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Loot

**Server-authoritative weighted loot tables for [SetNet](https://www.nuget.org/packages/SetNet).**

Define drop tables — guaranteed entries plus a number of weighted draws — and roll them **on the server**, where clients never see the weights or the RNG. Drops are granted through the shared [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) hub. Most loot is server-triggered (a mob dies, a chest is looted in game logic); a gated client-open path handles loot boxes. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Loot
```

## Usage

Call `InventoryRuntime.Enable()` and `LootRuntime.Enable()` once at startup.

```csharp
// server
InventoryRuntime.Enable(); LootRuntime.Enable();
var inventory = server.UseInventory();
var loot = server.UseLoot(inventory).Define(new LootTable("goblin", rolls: 2, entries: new[]
{
    new LootEntry("gold", 5, weight: 0, guaranteed: true),   // always drops
    new LootEntry("dagger",  1, weight: 10),
    new LootEntry("potion",  2, weight: 30),
    new LootEntry("gem",     1, weight: 1),                  // rare
}));

// game logic when the goblin dies:
var drops = await loot.RollAndGrantAsync(playerKey, "goblin");
```

Client-opened containers (opt in with a policy):

```csharp
var loot = server.UseLoot(inventory, new LootOptions
{
    CanOpen = (playerKey, tableId) => PlayerHoldsKeyFor(playerKey, tableId)
});

// client
var loot = client.UseLoot();
var drops = await loot.OpenAsync("crate");    // throws LootException if not permitted
PlayReveal(drops);
```

## API

**Server:** `server.UseLoot(InventoryServer, LootOptions?)` → `LootServer` — `Define(LootTable)`, `Roll(tableId)`, `RollAndGrantAsync(playerKey, tableId)`.
**Client:** `client.UseLoot()` → `LootClient` — `OpenAsync(tableId)`.
**Options:** `CanOpen` (default denies client opens), `Seed` (reproducible rolls for tests).

`InventoryRuntime.Enable()` + `LootRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Loot` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **RNG stays server-side.** Clients can't inspect or predict drops. Set `LootOptions.Seed` only in tests.
- **Client opens are denied by default.** Supply a `CanOpen` policy that verifies the player actually holds the container/key; otherwise roll from server game logic with `RollAndGrantAsync`.
- Guaranteed entries drop once per roll; weighted entries are drawn `Rolls` times by relative weight, and identical items merge into one stack.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
