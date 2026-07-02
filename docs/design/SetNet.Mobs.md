# SetNet.Mobs — design (not yet implemented)

**Status:** design only. Reserved wire types (tentative): **65442 / 65443 / 65444** (AI telegraphs / aggro / death); movement + HP replicate over `SetNet.StateSync` (reuses its wire types).
**Depends on:** `SetNet` + `SetNet.StateSync` (movement/state replication); optional `SetNet.StatusEffects` (debuffs), `SetNet.Loot` (drops), `SetNet.Progression` (kill XP).

## Goal

The same uniform-entity idea as `SetNet.NPC`, but for **hostile AI entities**, with the extra requirement that **each mob type gets its own AI**: one aggros on sight, one only retaliates when hit, one kites and shoots from range, one is a caster that picks spells by situation. The framework must make writing a new AI *uniform and composable* — a mob author writes one `IMobBrain` (or composes reusable behaviour components) and the framework handles the tick loop, perception, threat, movement authority, ability cooldowns/telegraphs, replication, death, loot, and respawn.

Everything is **server-authoritative**: the client renders replicated mob state and sends only its own actions (which the server validates). No AI or combat math ever runs on the client.

## Core model

```
MobInstance
  string  Id, Type
  Vector3 Position, Velocity
  string  Zone
  double  Health, MaxHealth
  string  Faction                 // mobs ignore same-faction; players are a faction too
  ThreatTable Threat              // playerKey → accumulated threat
  string? Target                  // current target player key
  MobCastState? Casting           // ability + remaining cast time (telegraphed to observers)
  IDictionary<string,object> Blackboard   // brain scratch (patrol point, last-known target pos, phase)

IMobBrain                         // ONE per mob type — this is where per-mob AI lives
  string MobType { get; }
  void  OnSpawn(MobContext ctx);
  Task  ThinkAsync(MobContext ctx, MobSenses senses);     // called every AI tick
  Task  OnDamagedAsync(MobContext ctx, DamageEvent dmg);  // reactive: someone hit me
  Task  OnTargetLostAsync(MobContext ctx);                // target died / fled past leash
  Task  OnDeathAsync(MobContext ctx, string? killerKey);
```

### MobContext — the brain's action surface

The brain never mutates the world directly; it *intends* actions and the framework resolves them (so movement stays authoritative and abilities respect cooldowns/cast times):

```
MobContext
  MobInstance Mob
  // intents:
  void  MoveTo(Vector3 pos);                 // pathing/steering handled by the movement layer
  void  Face(Vector3 pos);
  void  SetTarget(string? playerKey);
  Task  UseAbilityAsync(string abilityId, string targetKey);   // starts cast if off cooldown & in range
  void  Say(string emote);                   // flavor broadcast to observers
  IServiceProvider Services;                  // StatusEffectServer, LootServer, ProgressionServer, app HP sink…
```

### MobSenses — perception snapshot (computed by the framework each tick)

```
MobSenses
  IReadOnlyList<PerceivedPlayer> Nearby;   // playerKey, distance, hasLineOfSight, threat
  PerceivedPlayer? Target;                 // resolved current target (if any, still valid)
  bool InLeashRange;                       // still within LeashRadius of spawn
  double HealthFraction;                   // Mob.Health / MaxHealth
```

Perception is pluggable (`IMobPerception`): default is radius + optional LOS callback; a spatial-grid implementation scales to many mobs (reuses `SetNet.StateSync.SpatialGrid` ideas).

## Per-mob AI — the four requested archetypes

These ship as ready-made brains **and** demonstrate the pattern; a game writes more the same way.

1. **AggressiveBrain** (`AggroRadius`, `AttackRange`, `LeashRadius`)
   `ThinkAsync`: if no target, pick the highest-threat (or nearest) `Nearby` player inside `AggroRadius` → `SetTarget`. If target beyond `AttackRange`, `MoveTo(target)`. If within, `UseAbilityAsync("melee", target)`. If `!InLeashRange`, drop target, reset toward spawn, regen.

2. **PassiveRetaliateBrain** (peaceful until provoked)
   `ThinkAsync` idles/patrols; **`OnDamagedAsync`** adds the attacker to the threat table and calls `SetTarget(dmg.Source)` — from then on it behaves aggressively toward attackers, leashing/reset on `OnTargetLostAsync`. This is the "only fights back when hit" mob and shows why the reactive hook exists separately from the tick.

3. **RangedBrain** (`MinRange`, `MaxRange`, kiting)
   Keeps the target in a band: if `distance < MinRange`, `MoveTo` *away* (kite); if `distance > MaxRange`, close in; when inside the band with LOS, `UseAbilityAsync("shoot", target)`. Retreats toward allies when cornered (no room to kite).

4. **CasterBrain** (ability rotation by situation)
   Each tick, choose an ability from a priority list guarded by predicates:
   - `HealthFraction < 0.3` and `heal` off cooldown → `UseAbilityAsync("heal", self)`;
   - `≥ 3` players clustered within an AoE radius → `UseAbilityAsync("firestorm", target)`;
   - else single-target → `UseAbilityAsync("bolt", target)`.
   Casts are **telegraphed**: `UseAbilityAsync` starts a `MobCastState` with a cast time replicated to observers (so players can interrupt/dodge); the framework tracks cooldowns and applies effects via `SetNet.StatusEffects` on completion.

### Composability

Brains are built from reusable **behaviour components** running in a fixed pipeline — `Perceive → SelectTarget → Position(Approach|Kite|Leash) → Act(UseAbility)` — so most mobs are *config, not code*:

```
var goblin = MobBrain.Compose("goblin")
    .Perceive(aggroRadius: 12, requireLos: true)
    .SelectTarget(Threat.Highest)
    .Position(Approach.Melee(range: 2), leash: 25)
    .Act(Ability.OnCooldown("slash"));
```

A `MobBrainBuilder` (state machine / tiny behaviour tree) covers phases (e.g. a boss that swaps rotation at 50% HP); anything it can't express, you drop to a hand-written `IMobBrain`. Either way registration/spawn is identical.

## Abilities, damage, death

```
MobAbility { Id, Range, CooldownMs, CastTimeMs, Damage, string? EffectId /*applies via StatusEffects*/,
             AoeRadius /*0 = single-target*/ }
```

- The framework enforces range + cooldown + cast time; on cast completion it computes affected targets and raises a **`MobAttack { mobId, abilityId, targets[], damage }`**. **Damage resolution stays app-owned** (HP models vary wildly) through an `IDamageSink` in `Services` — the framework decides *intent and timing*, the game decides *what a hit does to a player*. Player→mob damage flows the other way: the client sends a validated attack; the server checks range/cooldown, subtracts `Mob.Health`, and feeds the mob's `OnDamagedAsync` + threat table.
- **Death:** `Health ≤ 0` → `OnDeathAsync(killer)` → framework rolls the mob type's `SetNet.Loot` table and grants to the killer / threat-topper, awards `SetNet.Progression` XP, broadcasts a death event, despawns, and schedules a respawn after `RespawnMs` (optionally at the spawn point).

## Tick loop, replication, scale

- A fixed-rate **mob tick** (`Timer`, default 10 Hz, configurable) iterates active mobs: build `MobSenses`, call `ThinkAsync`, advance movement/casts. Reactive hooks (`OnDamaged`) fire off the combat path, not the tick.
- **Replication:** a mob is a `SetNet.StateSync` entity (archetype: position, velocity, health, target, cast id + remaining). Nearby players observe it via StateSync interest (`DistanceInterest` / `SpatialGrid`); `SetNet.StatusEffects` debuffs on the mob are visible to watchers. This is why Mobs depends on StateSync rather than shipping its own snapshotting.
- **Sleep when unobserved:** mobs with no players in interest range **stop thinking** (tick skips them) and wake on the next nearby player or on damage — the key optimization for large worlds. `log()`-style visibility into how many mobs are awake helps tuning.
- **Node-locality:** mobs live on the node owning their zone; AI cost scales with awake-mob count per node (pairs with `SetNet.Sharding`/`SetNet.LoadBalancer`).

## Wire protocol

Movement/HP/cast state ride `SetNet.StateSync` snapshots (no new types). Mobs-specific pushes take a small reserved block:

- **65444 Command** (client→server): `Attack { mobId, abilityId }` (validated server-side), `Interact` is *not* here — mobs aren't NPCs.
- **65443 Reply:** attack acknowledgement / rejection (out of range, on cooldown).
- **65442 Event:** `MobAttack` telegraph/resolution, `MobAggro { mobId, targetKey }`, `MobDeath { mobId, killerKey, position }` — for clients to play VFX the raw StateSync snapshot doesn't convey.

## Relationship to SetNet.NPC

Both are server-authoritative typed entities with id/type/position/zone, spawn/despawn, and interest — that shared substrate should live in a thin internal **`SetNet.Entities`** primitive both build on, so NPCs and mobs appear in one interest/replication stream and a zone can hold both. NPC adds *interaction* (`INpcBehaviour`, request/reply, capability hand-off); Mobs adds *autonomy* (`IMobBrain`, tick loop, perception, threat, combat, StateSync replication). They're deliberately separate packages: an NPC has no AI/combat, a mob has no player interaction, and most games want one without the other.

## Open questions

- Pathing: ship a simple steering/nav-grid, require the app to provide `IPathfinder`, or lean entirely on `MoveTo` + app movement? Lean: `IPathfinder` seam with a naive straight-line default.
- Damage authority split: is the `IDamageSink` seam enough, or should Mobs own a minimal HP model to be useful out-of-the-box? Lean: minimal built-in HP with an opt-out sink for custom combat.
- Determinism: for competitive/lockstep games, the AI tick + RNG (target ties, ability rolls) should be seedable — expose a per-mob RNG seed like `SetNet.Loot` does.
