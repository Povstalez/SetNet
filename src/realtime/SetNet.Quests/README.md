<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Quests

**Server-authoritative quests for [SetNet](https://www.nuget.org/packages/SetNet).**

Define quests as a set of objectives plus item rewards. Players accept them; **server game logic reports progress** by objective key (which advances every accepted quest sharing that key); a `QuestCompleted` event fires the moment all objectives are met; and rewards are granted on claim through the shared [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) hub. Clients accept, claim, and watch progress — they never move their own counters. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Quests
```

## Usage

Call `InventoryRuntime.Enable()` and `QuestRuntime.Enable()` once at startup.

```csharp
// server
InventoryRuntime.Enable(); QuestRuntime.Enable();
var inventory = server.UseInventory();
var quests = server.UseQuests(inventory).Define(new QuestDefinition("goblin_hunt",
    objectives: new[] { new QuestObjective("kill_goblin", 10), new QuestObjective("collect_ear", 5) },
    rewards:    new[] { new ItemStack("gold", 100), new ItemStack("goblin_slayer_badge", 1) }));

quests.QuestCompleted += (playerKey, questId) => Log($"{playerKey} finished {questId}");

// game logic when a goblin dies near a player who accepted the quest:
await quests.ProgressAsync(playerKey, "kill_goblin");

// client
InventoryRuntime.Enable(); QuestRuntime.Enable();
var quests = client.UseQuests();
quests.Updated += v => DrawQuestLog(v);
await quests.AcceptAsync("goblin_hunt");
// ... later, when v.Completable:
await quests.ClaimAsync("goblin_hunt");
```

## API

**Server:** `server.UseQuests(InventoryServer, IQuestStore?)` → `QuestServer` — `Define`, `AcceptAsync`, `ProgressAsync`, `ClaimAsync`, `AbandonAsync`, `ViewsAsync`, `event QuestCompleted`.
**Client:** `client.UseQuests()` → `QuestClient` — `AcceptAsync`, `AbandonAsync`, `ClaimAsync`, `ListAsync`, `event Updated`.

`InventoryRuntime.Enable()` + `QuestRuntime.Enable()` — one-time bootstrap.

## Notes

- **Reserved wire types 65462 / 65463 / 65464.** Don't reuse them.
- **Progress is server-driven.** Call `ProgressAsync(playerKey, objectiveKey)` from game logic; it advances every accepted, unclaimed quest that has that objective, capped at the requirement.
- **Rewards.** Item rewards grant through `SetNet.Inventory` on claim. For XP/currency rewards, handle `QuestCompleted` (or the client's claim ack) and call [`SetNet.Progression`](https://www.nuget.org/packages/SetNet.Progression) / [`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet).
- **Persistence.** Default `MemoryQuestStore` is per-process; implement `IQuestStore` over Redis/SQL for durable quest logs.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
