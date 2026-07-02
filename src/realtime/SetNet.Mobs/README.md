<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Mobs

**Server-authoritative hostile AI entities for [SetNet](https://www.nuget.org/packages/SetNet).**

Each mob type gets its own AI — one aggros on sight, one only retaliates when hit, one kites and shoots from range, one is a caster that picks spells by situation. You write one `IMobBrain` per type (or compose one from behaviour components) and the framework handles the tick loop, perception, threat, movement authority, ability cooldowns/casts/telegraphs, damage, death and respawn.

Everything is **server-authoritative**: the client renders replicated mob state and sends only its own attacks (which the server validates). No AI or combat math ever runs on the client. Added by **composition** — no base class.

Replication is a **seam** (`IMobReplication`) — Mobs has *no* dependency on any replication package. The default is a no-op; you either poll `MobServer.Mobs` / handle `MobMoved`, or plug an adapter such as [`SetNet.Mobs.StateSync`](https://www.nuget.org/packages/SetNet.Mobs.StateSync) (see below).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Mobs
```

`SetNet.Mobs` depends on `SetNet` + `SetNet.GeoData` (world queries / `Vec3`) + `SetNet.PathFinding` (movement seam). It does **not** depend on `SetNet.StateSync`.

## Usage

Call `MobsRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — the authority:

```csharp
MobsRuntime.Enable();

// Register the abilities the framework enforces range/cooldown/cast for:
var opts = new MobOptions
{
    GeoData        = world,                      // optional: LOS + pathfinding
    PlayerPosition = key => positions.Get(key),  // the app knows where players are
    AllPlayers     = () => positions.Keys,
    TickRateHz     = 10,
};
opts.AddAbility(new MobAbility("melee", range: 2f, cooldownMs: 1500, damage: 8));
opts.AddAbility(new MobAbility("shoot", range: 18f, cooldownMs: 1200, damage: 6));
opts.AddAbility(new MobAbility("bolt",  range: 20f, cooldownMs: 1000, damage: 10, castTimeMs: 800));
opts.AddAbility(new MobAbility("firestorm", range: 20f, cooldownMs: 6000, damage: 14, castTimeMs: 1500, aoeRadius: 6f));
opts.AddAbility(new MobAbility("heal",  range: 0f, cooldownMs: 8000, damage: 0));

var mobs = server.UseMobs(opts);

// Register a brain per mob type, then spawn:
mobs.Register(new AggressiveBrain("goblin", aggroRadius: 12, attackRange: 2, leashRadius: 25));
mobs.Register(new RangedBrain("archer", minRange: 6, maxRange: 18));
var id = mobs.Spawn(new MobSpawn { Type = "goblin", Position = spawnPoint, Zone = "forest", Health = 60, RespawnMs = 30000 });
```

**Client** — render + attack:

```csharp
MobsRuntime.Enable();
var mobs = client.UseMobs();
mobs.MobSpawned       += m   => SpawnVfx(m.Id, m.Type, m.Position);
mobs.MobDespawned     += id  => RemoveVfx(id);
mobs.MobAggro         += a   => PlayAggroRoar(a.MobId);
mobs.MobAttackReceived+= atk => PlayAbilityVfx(atk.MobId, atk.AbilityId, atk.Targets);
mobs.MobDeath         += d   => PlayDeathVfx(d.MobId, d.Position);

await mobs.SendAttackAsync(mobId, "playerSlash");   // throws MobException if rejected (range/cooldown)
```

## The four archetype brains

| Brain | Behaviour |
|---|---|
| `AggressiveBrain` | Aggros on sight; picks highest-threat (else nearest), closes to melee range, attacks; drops target + heads home past leash. |
| `PassiveRetaliateBrain` | Idles until hit; `OnDamagedAsync` latches onto the attacker, then behaves aggressively. The "only fights back when hit" mob. |
| `RangedBrain` | Keeps the target in a `[MinRange, MaxRange]` band — kites when too close, closes when too far, shoots when in-band with LOS. |
| `CasterBrain` | Picks an ability by situation each tick: self-heal when low → AoE when players cluster → single-target bolt; casts are telegraphed. |

## Composing a brain from config

Most mobs are config, not code — build a brain from behaviour components in a fixed `Perceive → SelectTarget → Position → Act` pipeline:

```csharp
var goblin = MobBrain.Compose("goblin")
    .Perceive(aggroRadius: 12, requireLos: true)
    .SelectTarget(Threat.Highest)                 // or Threat.Nearest
    .Position(Approach.Melee(range: 2), leash: 25)  // or Approach.Kite(min: 6, max: 18)
    .Act(Ability.OnCooldown("slash"))
    .Build();
mobs.Register(goblin);
```

Anything the pipeline can't express, drop to a hand-written `IMobBrain` (or subclass `MobBrain` for no-op defaults) — registration and spawn are identical either way.

## Damage, death, respawn

- The framework enforces **range + cooldown + cast time**. On cast completion it computes the affected targets and raises `MobAttack{mobId, abilityId, targets, damage}`, applying damage through an `IDamageSink` in `MobOptions.Services` (a minimal built-in player-HP model is used when you supply none).
- Player→mob damage flows the other way: the client `SendAttackAsync(mobId, abilityId)`; the server checks range/cooldown, subtracts `Mob.Health`, adds threat, and feeds the mob's `OnDamagedAsync`.
- `Health ≤ 0` → `OnDeathAsync(killer)` → a `MobDeath` event, optional loot/xp via an `IMobLootSink` in `Services`, despawn, and a respawn after `RespawnMs`.

## Ticking

By default the hub runs its own timer at `TickRateHz`. To drive it from your own game loop, set `UseInternalTimer = false` and call `MobServer.Update(dtMs)` yourself. Mobs with no players in interest range are skipped (**sleep when unobserved**); `MobServer.AwakeCount` reports how many ran their brain last tick.

## Replicating with StateSync

`SetNet.Mobs` deliberately does **not** depend on `SetNet.StateSync`. Movement/HP/cast state reaches clients through the `IMobReplication` seam, which is no-op by default. Two ways to replicate:

**1. Without StateSync — poll or subscribe.** Read `MobServer.Mobs` each frame, or handle the `MobMoved` / `MobUpdated` events, and push mob state however you already replicate the world:

```csharp
mobs.MobMoved += mob => myReplicator.Update(mob.Id, mob.Position, mob.Velocity, mob.Health);
foreach (var mob in mobs.Mobs) { /* render / snapshot */ }
```

**2. With [`SetNet.Mobs.StateSync`](https://www.nuget.org/packages/SetNet.Mobs.StateSync) — one line.** The adapter mirrors each mob into a `SetNet.StateSync` world as a `NetworkEntity` (interpolated position, velocity, health, target hash, cast id + remaining), so nearby players observe mobs through the same delta-compressed snapshot stream as everything else:

```bash
dotnet add package SetNet.StateSync
dotnet add package SetNet.Mobs.StateSync
```

```csharp
using SetNet.Mobs.StateSync;   // brings in the StateSyncReplication() extension

StateSyncRuntime.Enable();
MobsRuntime.Enable();

var world = server.UseStateSync();                          // your StateSync replication world

server.UseMobs(new MobOptions
{
    Replication    = world.StateSyncReplication(),          // <-- mobs now replicate over StateSync
    GeoData        = geo,
    PlayerPosition = key => positions.Get(key),
    AllPlayers     = () => positions.Keys,
});
```

On the client, register the identical mob archetype schema so the field indices line up:

```csharp
ReplicaRegistry.Register(StateSyncMobReplication.BuildSchema());   // same archetype id on both ends
```

Nearby players must be observers of the StateSync world (they normally already are). Without the adapter, fall back to polling `MobServer.Mobs` / handling `MobMoved` as in option 1.

## Wire protocol

Rides the unified [`SetNet.Protocol`](https://www.nuget.org/packages/SetNet) on the `Channels.Mobs` channel — no per-module wire types. Commands: `Attack` (client→server, validated → accepted/rejected). Events (server→client, interest-filtered where spatial): `MobSpawned`, `MobDespawned`, `MobAttack`, `MobAggro`, `MobDeath`.

## License

MIT
