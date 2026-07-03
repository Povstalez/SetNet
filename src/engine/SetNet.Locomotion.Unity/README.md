<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet Locomotion for Unity

**A client-side `NavAgent` that walks your GameObject along a path at your speed.**

The server (with [SetNet.Locomotion](https://www.nuget.org/packages/SetNet.Locomotion)) stays authoritative and sends
you only a **destination point**. On the client you compute the path yourself with `SetNet.PathFinding` over your local
geodata, hand the waypoints to `NavAgent.SetPath(...)`, and it moves the model there at `Speed` (your replicated
move-speed), turning to face the way it goes. This is the L2-style split: **server decides the point, client re-paths
and animates.**

## Use

```csharp
// drop the NavAgent component on your character/mob prefab, then:
var agent = GetComponent<NavAgent>();
agent.Speed = replicatedMoveSpeed;              // from your move-speed stat (replicated via StateSync/NetworkVariable)

// when the server sends "move to (x, z)":
var path = SetNet.PathFinding.Pathfinding.For(localGeo)
    .FindPath(ToVec3(transform.position), new SetNet.GeoData.Vec3(x, y, z));

var waypoints = new List<Vector3>();
foreach (var w in path.Waypoints) waypoints.Add(new Vector3(w.X, w.Y, w.Z));   // Vec3 → Vector3 (component-wise)
agent.SetPath(waypoints);

agent.Arrived += () => { /* play idle */ };
```

`NavAgent` is pure UnityEngine (no SetNet dependency) — it just walks a `List<Vector3>`. Convert your
`SetNet.GeoData.Vec3` waypoints to `Vector3` at the edge (they share X/Y/Z).

## Component fields

- **Speed** — world units/sec; set from your replicated move-speed stat.
- **ArriveDistance** — how close counts as reaching a waypoint.
- **TurnSpeed** — degrees/sec to face the movement direction (0 = snap, &lt; 0 = don't rotate).
- **`SetPath(waypoints)`** / **`Stop()`** / **`IsMoving`** / **`Destination`** / **`Arrived`** event.

## Notes

- **Client needs local geodata** — bake the same `.geo` for the client (see [SetNet.GeoData.Unity](https://github.com/Povstalez/SetNet)) so it can `FindPath` from the point.
- **Prediction / dead-reckoning** — this is where you extrapolate the owned character's movement smoothly instead of snapping to streamed positions; the server corrects if it disagrees.
- **UPM source**, editor + runtime, no NuGet, no SetNet assembly required.

## License

MIT
