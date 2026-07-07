using System.Collections.Generic;
using SetNet.GeoData;
using SetNet.Hitscan;
using SetNet.Projectiles;
using SetNet.Ticks;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Covers SetNet.Hitscan (pluggable IHitDetector, sphere targets, composite closest, GeoData world) and SetNet.Projectiles (swept flight, hit, expiry, tick).</summary>
    public class HitscanProjectilesTests
    {
        // A target provider you control.
        private sealed class TargetBag : ITargetProvider
        {
            public readonly List<HitTarget> List = new();
            public IEnumerable<HitTarget> Targets(in HitQuery query) => List;
        }

        // A stand-in "world" detector reporting a hit at a fixed distance (or a miss).
        private sealed class FakeWorld : IHitDetector
        {
            public float? Distance;
            public HitResult Raycast(in Ray ray, in HitQuery query)
                => Distance is { } d && d <= query.MaxDistance
                    ? new HitResult(HitKind.World, ray.At(d), d, new Vec3(0, 1, 0))
                    : HitResult.Miss;
        }

        [Fact]
        public void Target_detector_hits_the_sphere_and_ignores_the_shooter_and_misses()
        {
            var tp = new TargetBag();
            tp.List.Add(new HitTarget("mob1", new Vec3(0, 0, 10), 1f, tag: "wolf"));
            tp.List.Add(new HitTarget("player", new Vec3(0, 0, 3), 1f));   // the shooter — must be ignored
            var det = new TargetHitDetector(tp);

            var hit = det.Raycast(new Ray(Vec3.Zero, new Vec3(0, 0, 1)), new HitQuery("player", 100));
            Assert.True(hit.Hit);
            Assert.Equal(HitKind.Target, hit.Kind);
            Assert.Equal("mob1", hit.TargetId);
            Assert.Equal("wolf", hit.Target);
            Assert.Equal(9f, hit.Distance, 2);                            // front of the sphere at z=9

            var miss = det.Raycast(new Ray(Vec3.Zero, new Vec3(1, 0, 0)), new HitQuery("player", 100));
            Assert.False(miss.Hit);
        }

        [Fact]
        public void Composite_returns_the_closest_hit_so_a_wall_blocks_a_target_behind_it()
        {
            var tp = new TargetBag();
            tp.List.Add(new HitTarget("mob1", new Vec3(0, 0, 10), 1f));   // target at ~9
            var world = new FakeWorld { Distance = 5f };                  // wall at 5

            var composite = new CompositeHitDetector(new TargetHitDetector(tp), world);
            var hit = composite.Raycast(new Ray(Vec3.Zero, new Vec3(0, 0, 1)), new HitQuery("player", 100));

            Assert.Equal(HitKind.World, hit.Kind);                        // wall is closer → blocks the target
            Assert.Equal(5f, hit.Distance, 2);
        }

        [Fact]
        public void GeoData_detector_reports_a_world_hit()
        {
            var geo = new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 12, depth: 12)
                .Fill((x, z) => x == 6 ? (false, true, 0f) : (true, false, 0f)).Build();   // a wall column at x=6
            var det = new GeoDataHitDetector(geo);

            var hit = det.Raycast(new Ray(new Vec3(0.5f, 0f, 0.5f), new Vec3(1, 0, 0)), new HitQuery("p", 20));
            Assert.True(hit.Hit);
            Assert.Equal(HitKind.World, hit.Kind);
        }

        [Fact]
        public void Hitscan_fire_raises_events_and_uses_a_custom_detector()
        {
            var world = new FakeWorld { Distance = 4f };
            var hitscan = new HitscanResolver(world);                            // ← ANY IHitDetector, including your own
            HitResult? got = null;
            hitscan.OnHit += r => got = r;

            var result = hitscan.Fire(Vec3.Zero, new Vec3(0, 0, 1), "player", maxDistance: 50);
            Assert.True(result.Hit);
            Assert.NotNull(got);
            Assert.Equal(4f, got!.Value.Distance, 2);
        }

        // ---- Projectiles ----

        private sealed class NoHits : IHitDetector
        {
            public HitResult Raycast(in Ray ray, in HitQuery query) => HitResult.Miss;
        }

        [Fact]
        public void Projectile_flies_and_hits_a_target_along_the_swept_path()
        {
            var tp = new TargetBag();
            tp.List.Add(new HitTarget("mob1", new Vec3(0, 0, 10), 0.5f));
            var world = new ProjectileSystem(new TargetHitDetector(tp), new ProjectileOptions { AutoTick = false });

            Projectile? hitProj = null; HitResult hitRes = default;
            world.Hit += (p, r) => { hitProj = p; hitRes = r; };

            world.Spawn(new ProjectileSpawn { Origin = Vec3.Zero, Direction = new Vec3(0, 0, 1), Speed = 20f, OwnerId = "player" });

            for (var i = 0; i < 20 && hitProj == null; i++) world.Update(50);   // 50ms steps

            Assert.NotNull(hitProj);
            Assert.Equal("mob1", hitRes.TargetId);
            Assert.Equal(0, world.Count);                                       // removed after the hit
        }

        [Fact]
        public void Projectile_expires_on_lifetime_without_a_hit()
        {
            var world = new ProjectileSystem(new NoHits(), new ProjectileOptions { AutoTick = false });
            var expired = false;
            world.Expired += _ => expired = true;

            world.Spawn(new ProjectileSpawn { Origin = Vec3.Zero, Direction = new Vec3(0, 0, 1), Speed = 10f, LifetimeMs = 100 });
            world.Update(60);
            Assert.False(expired);
            world.Update(60);                                                   // total 120ms > 100ms lifetime
            Assert.True(expired);
            Assert.Equal(0, world.Count);
        }

        [Fact]
        public void Gravity_curves_the_projectile_down()
        {
            var world = new ProjectileSystem(new NoHits(),
                new ProjectileOptions { AutoTick = false, Gravity = new Vec3(0, -20f, 0) });
            var p = world.Spawn(new ProjectileSpawn { Origin = new Vec3(0, 10, 0), Direction = new Vec3(0, 0, 1), Speed = 10f, LifetimeMs = 0, MaxDistance = 0 });

            for (var i = 0; i < 10; i++) world.Update(50);   // 0.5s under gravity
            Assert.True(p.Position.Y < 10f);                 // dropped
            Assert.True(p.Velocity.Y < 0f);
        }

        [Fact]
        public void Projectile_auto_ticks_through_the_scheduler()
        {
            var prev = TickHost.Current;
            try
            {
                var ticks = new TickScheduler().MakeCurrent();
                var tp = new TargetBag();
                tp.List.Add(new HitTarget("mob1", new Vec3(0, 0, 8), 0.5f));
                var world = new ProjectileSystem(new TargetHitDetector(tp), new ProjectileOptions { Hz = 20 });  // auto-subscribes

                var hit = false;
                world.Hit += (_, __) => hit = true;
                world.Spawn(new ProjectileSpawn { Origin = Vec3.Zero, Direction = new Vec3(0, 0, 1), Speed = 20f, OwnerId = "player" });

                for (var i = 0; i < 40 && !hit; i++) ticks.Pump(50);   // driven via SetNet.Ticks
                Assert.True(hit);
                world.Dispose();
            }
            finally { TickHost.Current = prev; }
        }
    }
}
