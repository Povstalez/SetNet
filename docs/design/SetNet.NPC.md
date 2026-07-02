# SetNet.NPC — design (not yet implemented)

**Status:** design only. Reserved wire types (tentative): **65445 / 65446 / 65447**.
**Depends on:** `SetNet` only (composes with Vendor / Quests / StatusEffects / Zones / Wallet at the *app* layer, not by hard reference).

## Goal

A **unified abstraction for non-living, interactive entities** — vendors, bankers, quest-givers, healers, buffers, teleporters, trainers, flight masters. Every such NPC differs only in *what happens when a player interacts with it*, so the framework should standardize everything **around** that (registration, spawning, interest, the interact request/response, range/rate validation) and let the developer write just the **interaction logic** behind one interface. Two NPCs are then implemented the same way — register a behaviour, spawn instances — regardless of whether one sells swords and another hands out quests.

Non-goals: NPC pathing/AI/combat (that's `SetNet.Mobs`), dialogue *content* authoring (the behaviour owns its dialogue), and reimplementing the economy modules (an NPC *delegates* to Vendor/Quests/etc.).

## Core model

```
NpcInstance                     // one spawned NPC in the world
  string   Id                   // unique instance id
  string   Type                 // behaviour key, e.g. "blacksmith"
  Vector3  Position             // world position (for range checks / interest)
  string   Zone                 // owning zone/node (pairs with Sharding/Zones)
  byte[]   Metadata             // opaque display data sent to clients (name, model, icon…)
  IDictionary<string,object> State   // server-only scratch (daily stock seed, cooldowns) — never sent

INpcBehaviour                   // the ONE thing a developer writes per NPC type
  string NpcType { get; }
  Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction request);
  Task OnSpawnAsync(NpcContext ctx)   => Task.CompletedTask;   // optional
  Task OnDespawnAsync(NpcContext ctx) => Task.CompletedTask;   // optional

NpcInteraction                  // client → NPC
  string Action                 // "open" | "talk" | "accept_quest" | "buff" | …  (behaviour-defined)
  byte[] Payload                // action args (a chosen dialogue option id, quest id, …)

NpcResponse                     // NPC → client
  bool     Ok
  string   Message              // human text / error
  byte[]   Payload              // action result (dialogue node, list, …)
  string?  Capability           // OPTIONAL hand-off hint, e.g. "vendor:blacksmith", "bank:personal"
```

The **capability hand-off** is the key composition trick: an NPC doesn't re-implement a shop. `VendorNpcBehaviour.OnInteractAsync("open")` returns `Capability = "vendor:blacksmith"`, and the client, seeing that, opens its existing `SetNet.Vendor` UI against vendor id `blacksmith`. The NPC layer stays a thin, uniform dispatcher; each domain module keeps owning its logic and its own reserved wire types. Behaviours that have an *immediate* server-side effect (a buffer) skip the hand-off and just act + return `Ok`.

## NpcContext — what a behaviour can do

`NpcContext` is the behaviour's whole world, so behaviours never touch statics:

```
NpcContext
  NpcInstance Npc                     // the instance being interacted with
  BasePeer    Peer                    // the interacting player's connection
  string      PlayerKey               // resolved via the shared player-key resolver
  IServiceProvider Services           // resolve the hubs the app registered (InventoryServer, WalletServer,
                                       //   VendorServer, QuestServer, StatusEffectServer, ZonesServer, …)
```

Behaviours pull whatever hubs they need from `Services` (populated at `UseNpc(...)` time or via `SetNet.DependencyInjection`). This keeps `SetNet.NPC` dependency-free while letting a `HealerNpcBehaviour` call `StatusEffectServer.ApplyAsync(...)` and charge `WalletServer.TryWithdrawAsync(...)`.

## Server API

```
var npc = server.UseNpc(new NpcOptions { PlayerKey = …, Services = provider });   // → NpcServer

npc.Register(new VendorNpcBehaviour());          // one behaviour per Type
npc.Register(new QuestGiverNpcBehaviour());
npc.Register(new BufferNpcBehaviour());

var id = npc.Spawn(new NpcSpawn {
    Type = "blacksmith", Position = pos, Zone = "town", Metadata = Encode(name:"Borin", model:"dwarf") });
npc.Despawn(id);
npc.InstancesInZone("town");                     // for tooling / respawn
```

`NpcServer` also exposes an `INpcInterest` (default `AllInterest`; `ZoneInterest` for larger worlds) that decides which instances each client is told about, and a `Func<string playerKey, NpcInstance, bool> CanInteract` gate (default: always; a distance/LOS check for anti-cheat, or a faction/level check).

## Client API

```
var npc = client.UseNpc();                       // → NpcClient
npc.NpcSpawned   += inst => AddToWorld(inst);     // interest-scoped
npc.NpcDespawned += id   => Remove(id);
npc.Nearby;                                        // current known instances

var resp = await npc.InteractAsync(npcId, action:"open");
if (resp.Capability?.StartsWith("vendor:") == true)
    await OpenVendorUi(client.UseVendor(), resp.Capability.Substring("vendor:".Length));
```

Discovery of instances is push-based (spawn/despawn events scoped by interest as the player moves between zones — `npc.EnterZoneAsync(zone)` / `LeaveZoneAsync`), mirroring how `SetNet.Rooms` pushes membership.

## Wire protocol

Reserved **Command 65447 / Reply 65446 / Event 65445**, hand-framed `byte[]` like Rooms/Relay:

- **Command** (client→server): `Interact { npcId, action, payload }`, `EnterZone { zone }`, `LeaveZone { zone }`.
- **Reply** (server→client, correlated): the `NpcResponse`, or the zone's instance list.
- **Event** (server→client, push): `NpcSpawned { instance }`, `NpcDespawned { id }` — filtered by the client's interest set (co-located clients filter by their own interest, same pattern as Trade's `RecipientKey`).

Interaction is correlated request/reply (same `TaskCompletionSource` registry pattern as every other module). `CanInteract` is checked server-side before the behaviour runs; failures come back as `Ok=false`.

## Worked behaviours (all implemented identically)

| NPC | `OnInteractAsync` sketch |
|---|---|
| **Vendor** | `"open"` → `Ok`, `Capability="vendor:<vendorId>"` (client transacts via `SetNet.Vendor`) |
| **Banker** | `"open"` → `Capability="bank:personal"` / `"bank:guild:<id>"` (guild bank is a keyed `SetNet.Inventory`) |
| **Quest-giver** | `"talk"` → available quest list; `"accept"` (payload=questId) → `QuestServer.AcceptAsync`; `"turn_in"` → `ClaimAsync` |
| **Buffer** | `"buff"` → charge `WalletServer.TryWithdrawAsync`, then `StatusEffectServer.ApplyAsync(playerKey, "blessing", …)`, return `Ok` (no hand-off) |
| **Healer** | `"heal"` → app HP model restore (via a hub in `Services`) ± wallet charge |
| **Teleporter** | `"teleport"` (payload=destZone) → `ZonesServer.TransferAsync(peer, target, carryState)` |
| **Trainer** | `"talk"` → learnable skills; `"learn"` → `ProgressionServer`/skill hub side-effect |

A new NPC = a new `INpcBehaviour` + `npc.Register(...)` + `npc.Spawn(...)`. Nothing else changes; the client's interact/hand-off loop is generic.

## Cross-cutting

- **Range / anti-cheat:** `CanInteract` gets the player key and the `NpcInstance`; a distance check against replicated player position (or `SetNet.StateSync`) stops interact-from-anywhere. Compose with `SetNet.RateLimit` for spam.
- **Persistence:** NPCs are almost always spawned from world/config data at boot, so no store is required by default; an optional `INpcStore` covers dynamically-created persistent NPCs.
- **Node-locality:** instances live on the node that owns their zone (pairs with `SetNet.Sharding`/`SetNet.Zones`); a teleporter NPC is the natural bridge between nodes.
- **Shared with Mobs:** the entity registry + interest + spawn/despawn wire shape is common to `SetNet.Mobs`. Extract a thin internal `SetNet.Entities` primitive (instance id/type/position/zone + interest + spawn/despawn events) that both packages build on, so NPCs and mobs coexist in one interest stream.

## Open questions

- Dialogue trees: keep entirely inside the behaviour (action/option ping-pong), or offer an optional `DialogueGraph` helper the framework can walk? Lean: helper is optional sugar, not core.
- Interest source of truth: NPC-owned zone subscription vs. reusing `SetNet.StateSync` interest when the game already replicates the world. Support both via `INpcInterest`.
