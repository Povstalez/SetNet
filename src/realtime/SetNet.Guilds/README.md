<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Guilds

**Guilds / clans for [SetNet](https://www.nuget.org/packages/SetNet) — roles and a shared bank.**

Create or join a guild, manage members with three roles (member / officer / leader), and share a **guild bank** — which is just a guild-keyed inventory in the same [`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory) hub, so deposits and withdrawals are atomic item moves with the same anti-dupe guarantees. Anyone deposits; officers and the leader withdraw and kick; the leader promotes, transfers, and disbands. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Inventory
dotnet add package SetNet.Guilds
```

## Usage

Call `InventoryRuntime.Enable()` and `GuildRuntime.Enable()` once at startup.

```csharp
// server
InventoryRuntime.Enable(); GuildRuntime.Enable();
var inventory = server.UseInventory();
server.UseGuilds(inventory);

// client
InventoryRuntime.Enable(); GuildRuntime.Enable();
var guilds = client.UseGuilds();
guilds.MemberJoined += key => Log($"{key} joined");
guilds.MemberLeft   += key => Log($"{key} left");
guilds.Disbanded    += ()  => Log("guild disbanded");

string id = await guilds.CreateAsync("The Night Watch");   // creator = leader
// another player:
await guilds.JoinAsync(id);

await guilds.PromoteAsync(memberKey, GuildRole.Officer);
await guilds.BankDepositAsync("gold", 1000);               // anyone can deposit
await guilds.BankWithdrawAsync("gold", 200);               // officer/leader only
```

## API

**Server:** `server.UseGuilds(InventoryServer, IGuildStore?)` → `GuildServer`.
**Client:** `client.UseGuilds()` → `GuildClient`

| Member | Purpose |
|---|---|
| `CreateAsync(name)` / `JoinAsync(guildId)` / `LeaveAsync()` | membership |
| `PromoteAsync(memberKey, role)` / `KickAsync(memberKey)` | management (leader / officer+) |
| `ListMembersAsync()` | members + roles |
| `BankDepositAsync(itemId, count)` / `BankWithdrawAsync(...)` / `BankListAsync()` | shared bank |
| `event MemberJoined` / `MemberLeft` / `Disbanded` | membership changes |

`InventoryRuntime.Enable()` + `GuildRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Guilds` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **The bank is a keyed inventory.** It lives under `guild:<id>` in your `SetNet.Inventory` hub — deposits/withdrawals go through `TryRevokeAsync`/`GrantAsync`, so they're atomic and can't dupe. Persist it by using a durable `IInventoryStore`.
- **Role rules.** Deposit: any member. Withdraw / kick: officer or leader (and you can't kick an equal/higher rank). Promote / transfer / disband: leader. Promoting someone to `Leader` transfers leadership (you become officer).
- **Leaving.** A leader who leaves passes leadership to the highest-ranked remaining member; the last member leaving **disbands** the guild and gets the bank contents back (nothing is destroyed).
- **Persistence.** Default `MemoryGuildStore` is per-process; implement `IGuildStore` (and a durable `IInventoryStore`) for guilds and banks that survive restarts.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
