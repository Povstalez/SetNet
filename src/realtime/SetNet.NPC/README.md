<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.NPC

**Interactive non-living entities for [SetNet](https://www.nuget.org/packages/SetNet)** — vendors, buffers, teleporters, quest-givers, healers, trainers.

Every such NPC differs only in *what happens when a player interacts with it*, so this package standardizes everything **around** that — registration, spawning, zone interest, and the interact request/response — and lets you write just the interaction logic behind one interface. Two NPCs are then built the same way: register a behaviour, spawn instances. Added by **composition** — no base class.

The key trick is the **capability hand-off**: a behaviour doesn't re-implement a shop. `VendorNpcBehaviour` returns `Capability = "vendor:blacksmith"`, and the client, seeing that, opens its *existing* vendor UI. Behaviours with an immediate server-side effect (a buffer) skip the hand-off and just act + return `Ok`.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.NPC
```

## Usage

Call `NpcRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — register behaviours, spawn instances:

```csharp
NpcRuntime.Enable();
var npc = server.UseNpc(new NpcOptions { Services = provider });   // Services lets behaviours resolve app hubs

npc.Register(new VendorNpcBehaviour(vendorId: "blacksmith", npcType: "blacksmith"));
npc.Register(new TeleporterNpcBehaviour(defaultDestination: "dungeon", npcType: "portal"));
npc.Register(new BufferNpcBehaviour(buffId: "blessing", npcType: "priest"));   // needs an IBuffApplier in Services

var id = npc.Spawn(new NpcSpawn {
    Type = "blacksmith", Position = new Vec3(10, 0, 5), Zone = "town",
    Metadata = Encoding.UTF8.GetBytes("Borin") });
```

**Client** — discover, interact, follow the hand-off:

```csharp
NpcRuntime.Enable();
var npc = client.UseNpc();
npc.NpcSpawned   += inst => AddToWorld(inst);
npc.NpcDespawned += id   => RemoveFromWorld(id);

await npc.EnterZoneAsync("town");            // receive that zone's interest-scoped spawn/despawn stream

var resp = await npc.InteractAsync(npcId, action: "open");
if (resp.Ok && resp.Capability?.StartsWith("vendor:") == true)
    OpenVendorUi(resp.Capability.Substring("vendor:".Length));   // hand off to your existing SetNet.Vendor UI
```

## Writing a behaviour

A new NPC = a new `INpcBehaviour` + `Register` + `Spawn`. Nothing else changes.

```csharp
public sealed class HealerNpcBehaviour : INpcBehaviour
{
    public string NpcType => "healer";

    public async Task<NpcResponse> OnInteractAsync(NpcContext ctx, NpcInteraction req)
    {
        // pull whatever hubs the app registered — behaviours never touch statics
        var hp = (IHealthHub)ctx.Services.GetService(typeof(IHealthHub));
        await hp.RestoreAsync(ctx.PlayerKey);
        return NpcResponse.Success("You are healed.");   // immediate effect, no hand-off
    }
}
```

## API

**Server:** `server.UseNpc(NpcOptions?)` → `NpcServer`

| Member | Purpose |
|---|---|
| `Register(INpcBehaviour)` | one behaviour per NPC `Type` |
| `Spawn(NpcSpawn) → string id` | spawn an instance, push it to interested clients |
| `Despawn(id) → bool` | despawn + push |
| `InstancesInZone(zone)` / `InstanceById(id)` | tooling / respawn |
| `KeyOf(peer)` / `Interest` / `CanInteract` | identity + interest + the pre-interaction gate |

**Client:** `client.UseNpc()` → `NpcClient` — `InteractAsync(id, action, payload?)`, `EnterZoneAsync(zone)` / `LeaveZoneAsync(zone)`, `Nearby`, events `NpcSpawned` / `NpcDespawned`.

**Options** (`NpcOptions`): `PlayerKey` (peer → stable key, default = connection id), `Services` (provider handed to each behaviour), `Interest` (`AllInterest` default; `ZoneInterest` for larger worlds), `CanInteract` (server-side gate: `(playerKey, instance) → bool`, put a distance/faction check here for anti-cheat).

**Worked behaviours** (all implemented via the one interface):

| Behaviour | Interaction | Result |
|---|---|---|
| `VendorNpcBehaviour` | `"open"` | `Ok`, `Capability = "vendor:<id>"` (no side effect — client transacts via its own vendor module) |
| `BufferNpcBehaviour` | `"buff"` | resolves `IBuffApplier` from `Services`, applies the buff, returns `Ok` (no hand-off) |
| `TeleporterNpcBehaviour` | `"teleport"` (payload = dest zone) | `Ok`, `Capability = "teleport:<zone>"` (client drives the migration) |

`NpcRuntime.Enable()` — one-time bootstrap.

## Notes

- Rides the unified **SetNet.Protocol** messaging layer on the `Channels.Npc` channel — all modules share one envelope wire type, so there are **no per-module wire ids** to reserve. Serializer-agnostic: the control protocol is hand-framed `byte[]`; your `Metadata` and interaction `Payload` are opaque bytes carried through untouched.
- **Interest is push-based.** A client enters a zone and receives that zone's spawn/despawn stream; `AllInterest` (default) tells everyone about everything, `ZoneInterest` scopes to the subscribed zone. Clients also filter on their own side, so co-located clients don't cross-talk.
- **`CanInteract` runs before the behaviour.** A rejection (or a behaviour that throws) comes back to the client as an `NpcResponse` with `Ok = false` — only transport/timeout problems surface as an `NpcException`.
- **Composition, not hard dependencies.** `SetNet.NPC` depends only on `SetNet` + `SetNet.GeoData` (for `Vec3`). Behaviours reach the economy/quest/status modules through the `Services` provider, so the NPC layer stays thin and each domain module keeps owning its logic (and its own channel).
- **Node-locality.** Instances live on the node that owns their zone (pairs with `SetNet.Sharding` / `SetNet.Zones`); a teleporter NPC is the natural bridge between nodes.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
