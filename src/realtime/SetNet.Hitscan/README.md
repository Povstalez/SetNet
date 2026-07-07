# SetNet.Hitscan

Server-authoritative **instant-hit (hitscan) shooting** for SetNet — with a **fully pluggable hit test**.

The whole point is the `IHitDetector` interface: **you decide what a shot hits**. Bring your own collision, or compose the shipped detectors.

```csharp
using SetNet.Hitscan;

// ── bring your own hit test ──────────────────────────────
public sealed class MyDetector : IHitDetector
{
    public HitResult Raycast(in Ray ray, in HitQuery query)
    {
        // cast against YOUR world / entities however you like; return the closest hit or HitResult.Miss
        if (MyPhysics.Raycast(ray.Origin, ray.Direction, query.MaxDistance, out var hit, out var id))
            return new HitResult(HitKind.Target, hit.Point, hit.Distance, hit.Normal, id, hit.Entity);
        return HitResult.Miss;
    }
}

var gun = new HitscanResolver(new MyDetector());
gun.OnHit += r => Damage(r.TargetId!, weapon.Damage);
var result = gun.Fire(muzzle, aimDir, shooterId: "player", maxDistance: 80);
```

## Or use the shipped detectors

```csharp
// sphere targets (your entities) + world geometry, closest wins (a wall blocks a target behind it)
var detector = new CompositeHitDetector(
    new TargetHitDetector(myTargetProvider),      // ITargetProvider → HitTarget spheres
    new GeoDataHitDetector(geo));                 // SetNet.GeoData world raycast

var gun = new HitscanResolver(detector);
```

- **`IHitDetector.Raycast(in Ray, in HitQuery)`** → `HitResult` — the seam you implement (or reuse). Returns the **closest** hit within `HitQuery.MaxDistance`, or `HitResult.Miss`.
- **`ITargetProvider`** supplies `HitTarget` spheres (id + centre + radius + your payload) for the query; `TargetHitDetector` does the ray-vs-sphere.
- **`GeoDataHitDetector`** wraps `IGeoData.Raycast` for walls/cover.
- **`CompositeHitDetector`** merges detectors, returning the nearest.
- **`HitscanResolver`** adds ignore-self, default range, `OnShot`/`OnHit` events; `Fire(origin, dir, shooterId, maxDistance?, faction?, context?)` → `HitResult`.

`HitResult`: `Hit`, `Kind` (Target/World), `Point`, `Distance`, `Normal`, `TargetId`, `Target` (your entity). The same `IHitDetector` also drives **[`SetNet.Projectiles`](https://www.nuget.org/packages/SetNet.Projectiles)**, so your collision code is reused for both.

Depends on `SetNet.GeoData` (for `Vec3` + `IGeoData`). No wire — the app broadcasts the result its own way. **License:** MIT.
