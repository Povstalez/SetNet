# UnifiedMove

A player character **and** a mob move through the **same** `SetNet.Locomotion` tick, and the **one** `Started` hook
fires for both — the single place a real server sends just the destination point to nearby clients (L2-style). The mob
(`SetNet.Mobs` + `SetNet.Mobs.Locomotion`) chases the player and is advanced by the same `LocomotionSystem`.

```bash
dotnet run --project examples/UnifiedMove
```

No networking — everything ticks in one process so you can watch the unification. You'll see the player fire a move
event per "click", the mob's move events counted as it chases, the distance closing, and both positions advancing from
the single `loco.Update(...)`.

## The whole idea (server)

```csharp
var loco = new LocomotionSystem(geo);                 // ONE movement system

var player = loco.CreateMover(spawn, speed, owner: "player");   // the player is a Mover

var mobs = server.UseMobs(new MobOptions {
    Mover = loco.AsMobMover(),                         // ← mobs advance through the SAME system
    PlayerPosition = k => k == "player" ? player.Position : null,
    AllPlayers = () => new[] { "player" },
});

// ONE hook for BOTH — this is where you send just the point to clients:
loco.Started += m => SendMoveTo(m.Owner, m.Destination!.Value);   // m.Owner: "player" string or the MobInstance

// each tick advances everything:
loco.Update(dtMs);
await mobs.Update(dtMs);   // mob AI decides → mob.GoTo on the shared system
```

## The Unity client half (`NavAgent` drives both)

The server sends only `MoveTo{ id, point }` on `Started`. On the client you re-path **locally** and let a `NavAgent`
(from `SetNet.Locomotion.Unity`) walk the model — the **same code for the player's avatar and for mobs**:

```csharp
// one handler for every entity — player or mob:
void OnServerMoveTo(int entityId, Vector3 point)
{
    var go = _views[entityId];                        // the GameObject for this entity
    var agent = go.GetComponent<NavAgent>();
    agent.Speed = _moveSpeed[entityId];               // replicated move-speed (from Stats)

    var path = Pathfinding.For(_localGeo).FindPath(ToVec3(go.transform.position), ToVec3(point));
    var wp = new List<Vector3>();
    foreach (var w in path.Waypoints) wp.Add(new Vector3(w.X, w.Y, w.Z));
    agent.SetPath(wp);                                // NavAgent walks it at Speed
}
```

So the server unifies **decision + simulation** (one Locomotion tick, one `Started`), and the client unifies
**rendering** (one `NavAgent` per entity, fed by one handler) — players and mobs go through the exact same path.
