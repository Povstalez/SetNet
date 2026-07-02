<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Progression

**Server-authoritative levels and XP for [SetNet](https://www.nuget.org/packages/SetNet).**

Game logic awards XP by player key; the hub applies a **configurable level curve** (rolling over as many levels as the XP fills at once), fires a `LeveledUp` event per level so you can hand out rewards, and pushes the new level/XP to the client for the XP bar. Clients read and subscribe but never award themselves anything. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Progression
```

## Usage

Call `ProgressionRuntime.Enable()` once at startup on both ends.

```csharp
// server
ProgressionRuntime.Enable();
var progression = server.UseProgression(options: new ProgressionOptions
{
    XpForLevel = level => 100L * level,   // XP to go from `level` to `level+1`
    MaxLevel = 60,
});
progression.LeveledUp += (playerKey, newLevel) => GrantLevelReward(playerKey, newLevel);

await progression.GrantXpAsync(playerKey, 250);   // may cross several levels

// client
ProgressionRuntime.Enable();
var progression = client.UseProgression();
progression.Changed += s => DrawXpBar(s.Level, s.Xp, s.XpToNext);
var state = await progression.GetAsync();
```

## API

**Server:** `server.UseProgression(IProgressionStore?, ProgressionOptions?)` → `ProgressionServer` — `GrantXpAsync`, `GetAsync`, `event LeveledUp`, `KeyOf(peer)`.
**Client:** `client.UseProgression()` → `ProgressionClient` — `GetAsync()`, `event Changed`.
**Options:** `PlayerKey`, `XpForLevel(level)`, `MaxLevel`.

`ProgressionRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Progression` channel (all modules share one envelope wire type, `65447`) — no per-module wire ids to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`, your payloads use your `SetNetSerializer`.
- **Multi-level rollover.** A single large XP grant advances as many levels as it fills; `LeveledUp` fires once per level. XP is clamped to 0 at `MaxLevel`.
- **Rewards live in your handler.** `LeveledUp` is where you grant items ([`SetNet.Inventory`](https://www.nuget.org/packages/SetNet.Inventory)), currency ([`SetNet.Wallet`](https://www.nuget.org/packages/SetNet.Wallet)), or unlocks — this package only tracks the number.
- **Stable player key** (default = connection id) — override for durable progression, matching your other player-data modules.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
