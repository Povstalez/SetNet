using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.Hitscan
{
    /// <summary>A ray: an origin and a (normalized) direction.</summary>
    public readonly struct Ray
    {
        /// <summary>Where the ray starts.</summary>
        public Vec3 Origin { get; }
        /// <summary>The (unit) direction.</summary>
        public Vec3 Direction { get; }

        /// <summary>Creates a ray; the direction is normalized.</summary>
        public Ray(Vec3 origin, Vec3 direction)
        {
            Origin = origin;
            Direction = direction.LengthSquared > 1e-12f ? direction.Normalized : new Vec3(0, 0, 1);
        }

        /// <summary>The point at distance <paramref name="t"/> along the ray.</summary>
        public Vec3 At(float t) => Origin + Direction * t;
    }

    /// <summary>Context for a hit test: who is shooting, their faction, how far to test, and an app payload.</summary>
    public readonly struct HitQuery
    {
        /// <summary>The shooter's id (a detector should not hit the shooter itself).</summary>
        public string ShooterId { get; }
        /// <summary>The shooter's faction (for the app's friendly-fire policy).</summary>
        public string? Faction { get; }
        /// <summary>The maximum distance to test.</summary>
        public float MaxDistance { get; }
        /// <summary>An arbitrary app payload passed through to your detector.</summary>
        public object? Context { get; }

        /// <summary>Creates a query.</summary>
        public HitQuery(string shooterId, float maxDistance, string? faction = null, object? context = null)
        {
            ShooterId = shooterId ?? ""; MaxDistance = maxDistance; Faction = faction; Context = context;
        }
    }

    /// <summary>What a ray hit.</summary>
    public enum HitKind
    {
        /// <summary>Nothing.</summary>
        None = 0,
        /// <summary>An entity/target.</summary>
        Target = 1,
        /// <summary>World geometry.</summary>
        World = 2
    }

    /// <summary>The outcome of a hit test.</summary>
    public readonly struct HitResult
    {
        /// <summary>Whether anything was hit.</summary>
        public bool Hit { get; }
        /// <summary>What kind of thing was hit.</summary>
        public HitKind Kind { get; }
        /// <summary>The hit point.</summary>
        public Vec3 Point { get; }
        /// <summary>Distance from the ray origin to the hit.</summary>
        public float Distance { get; }
        /// <summary>Surface/impact normal.</summary>
        public Vec3 Normal { get; }
        /// <summary>The target's id when <see cref="Kind"/> is <see cref="HitKind.Target"/>, else null.</summary>
        public string? TargetId { get; }
        /// <summary>The target payload when a target was hit (your entity), else null.</summary>
        public object? Target { get; }

        /// <summary>Creates a hit result.</summary>
        public HitResult(HitKind kind, Vec3 point, float distance, Vec3 normal, string? targetId = null, object? target = null)
        {
            Hit = kind != HitKind.None;
            Kind = kind; Point = point; Distance = distance; Normal = normal; TargetId = targetId; Target = target;
        }

        /// <summary>A miss.</summary>
        public static readonly HitResult Miss = new HitResult(HitKind.None, Vec3.Zero, float.PositiveInfinity, Vec3.Zero);
    }

    /// <summary>
    /// <b>The hit-test seam.</b> Implement this to answer "does this ray hit anything, and what/where?" — against your own
    /// world, your own targets, your own broadphase. Return the <b>closest</b> hit within <see cref="HitQuery.MaxDistance"/>,
    /// or <see cref="HitResult.Miss"/>. Both <c>SetNet.Hitscan</c> and <c>SetNet.Projectiles</c> drive their shots through
    /// this one interface, so your collision logic is reused for both. Ship-in-the-box detectors are provided, but you can
    /// always plug your own.
    /// </summary>
    public interface IHitDetector
    {
        /// <summary>Casts a ray and returns the closest hit within the query's max distance, or <see cref="HitResult.Miss"/>.</summary>
        HitResult Raycast(in Ray ray, in HitQuery query);
    }

    /// <summary>A spherical target the built-in <see cref="TargetHitDetector"/> can test against.</summary>
    public readonly struct HitTarget
    {
        /// <summary>Target id.</summary>
        public string Id { get; }
        /// <summary>Sphere centre (e.g. the entity's chest).</summary>
        public Vec3 Center { get; }
        /// <summary>Sphere radius (the entity's hit size).</summary>
        public float Radius { get; }
        /// <summary>Your entity payload, returned on a hit.</summary>
        public object? Tag { get; }

        /// <summary>Creates a target.</summary>
        public HitTarget(string id, Vec3 center, float radius, object? tag = null)
        {
            Id = id; Center = center; Radius = radius; Tag = tag;
        }
    }

    /// <summary>Supplies the candidate targets for a query (e.g. the entities near the shot). Filter friendly-fire here if you like.</summary>
    public interface ITargetProvider
    {
        /// <summary>The targets to test for this query (already filtered by your rules; the shooter is skipped anyway).</summary>
        IEnumerable<HitTarget> Targets(in HitQuery query);
    }

    /// <summary>Ray-vs-sphere math shared by the detectors.</summary>
    public static class Intersections
    {
        /// <summary>Distance along the ray to the first intersection with the sphere, or -1 if none within <paramref name="maxDistance"/>.</summary>
        public static float RaySphere(in Ray ray, Vec3 center, float radius, float maxDistance)
        {
            var oc = ray.Origin - center;
            var b = Vec3.Dot(oc, ray.Direction);
            var c = Vec3.Dot(oc, oc) - radius * radius;
            if (c > 0 && b > 0) return -1;                       // origin outside and pointing away
            var disc = b * b - c;
            if (disc < 0) return -1;                             // misses the sphere
            var t = -b - MathF.Sqrt(disc);
            if (t < 0) t = 0;                                    // origin inside → hit at the origin
            return t <= maxDistance ? t : -1;
        }
    }

    /// <summary>A detector over a set of spherical targets from an <see cref="ITargetProvider"/> (returns the closest).</summary>
    public sealed class TargetHitDetector : IHitDetector
    {
        private readonly ITargetProvider _targets;

        /// <summary>Creates the detector.</summary>
        public TargetHitDetector(ITargetProvider targets) => _targets = targets ?? throw new ArgumentNullException(nameof(targets));

        /// <inheritdoc/>
        public HitResult Raycast(in Ray ray, in HitQuery query)
        {
            var best = HitResult.Miss;
            foreach (var target in _targets.Targets(query))
            {
                if (target.Id == query.ShooterId) continue;      // never hit yourself
                var t = Intersections.RaySphere(in ray, target.Center, target.Radius, query.MaxDistance);
                if (t < 0 || t >= best.Distance) continue;
                var point = ray.At(t);
                var normal = (point - target.Center).LengthSquared > 1e-9f ? (point - target.Center).Normalized : ray.Direction * -1f;
                best = new HitResult(HitKind.Target, point, t, normal, target.Id, target.Tag);
            }
            return best;
        }
    }

    /// <summary>A detector over world geometry via <see cref="IGeoData"/> (walls, floors, cover).</summary>
    public sealed class GeoDataHitDetector : IHitDetector
    {
        private readonly IGeoData _geo;

        /// <summary>Creates the detector.</summary>
        public GeoDataHitDetector(IGeoData geo) => _geo = geo ?? throw new ArgumentNullException(nameof(geo));

        /// <inheritdoc/>
        public HitResult Raycast(in Ray ray, in HitQuery query)
        {
            var hit = _geo.Raycast(ray.Origin, ray.Direction, query.MaxDistance);
            return hit.Hit ? new HitResult(HitKind.World, hit.Point, hit.Distance, hit.Normal) : HitResult.Miss;
        }
    }

    /// <summary>Combines several detectors and returns the <b>closest</b> hit — so world geometry can block a target behind it.</summary>
    public sealed class CompositeHitDetector : IHitDetector
    {
        private readonly IHitDetector[] _detectors;

        /// <summary>Creates the composite (e.g. <c>new CompositeHitDetector(targets, world)</c>).</summary>
        public CompositeHitDetector(params IHitDetector[] detectors) => _detectors = detectors ?? Array.Empty<IHitDetector>();

        /// <inheritdoc/>
        public HitResult Raycast(in Ray ray, in HitQuery query)
        {
            var best = HitResult.Miss;
            foreach (var d in _detectors)
            {
                var r = d.Raycast(in ray, in query);
                if (r.Hit && r.Distance < best.Distance) best = r;
            }
            return best;
        }
    }

    /// <summary>
    /// Server-authoritative instant-hit (hitscan) shooting. You give it an <see cref="IHitDetector"/> (yours or a shipped
    /// one); <see cref="Fire(Vec3,Vec3,string,float?,string?,object?)"/> resolves the shot and raises <see cref="OnHit"/>.
    /// </summary>
    public sealed class HitscanResolver
    {
        private readonly IHitDetector _detector;

        /// <summary>Default max range applied when <c>Fire</c> is called without one.</summary>
        public float DefaultMaxDistance { get; set; }

        /// <summary>Raised for every resolved shot (hit or miss).</summary>
        public event Action<HitResult>? OnShot;
        /// <summary>Raised only when a shot hits something.</summary>
        public event Action<HitResult>? OnHit;

        /// <summary>Creates the resolver over a hit detector.</summary>
        public HitscanResolver(IHitDetector detector, float defaultMaxDistance = 100f)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            DefaultMaxDistance = defaultMaxDistance;
        }

        /// <summary>Resolves a shot; returns the hit result (also raised via <see cref="OnShot"/>/<see cref="OnHit"/>).</summary>
        public HitResult Fire(Vec3 origin, Vec3 direction, string shooterId, float? maxDistance = null, string? faction = null, object? context = null)
        {
            var query = new HitQuery(shooterId, maxDistance ?? DefaultMaxDistance, faction, context);
            var ray = new Ray(origin, direction);
            var result = _detector.Raycast(in ray, in query);
            OnShot?.Invoke(result);
            if (result.Hit) OnHit?.Invoke(result);
            return result;
        }
    }
}
