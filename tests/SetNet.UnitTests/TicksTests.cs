using System.Collections.Generic;
using System.Linq;
using SetNet.Ticks;
using SetNet.BehaviorTree;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Spawning;
using SetNet.Mobs;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>
    /// Covers the central <see cref="TickScheduler"/>: per-channel rate (fixed timestep), priority ordering, pause /
    /// enable, unregistration, the anti-spiral cap, and that the library's game-loop systems plug in via the tick
    /// interfaces (Locomotion/Spawning = ITickable, Mobs = IAsyncTickable, BehaviorTree via Bind).
    /// </summary>
    public class TicksTests
    {
        private sealed class Box { public int N; }

        [Fact]
        public void Channels_tick_at_their_own_rate()
        {
            var s = new TickScheduler();
            int slow = 0, fast = 0;
            s.Channel("slow", hz: 10).Add(_ => slow++);   // step 100ms
            s.Channel("fast", hz: 50).Add(_ => fast++);   // step  20ms

            // 100 pumps of 10ms = 1000ms of wall clock, driven incrementally.
            for (int i = 0; i < 100; i++) s.Pump(10);

            Assert.Equal(10, slow);   // 1000ms / 100ms
            Assert.Equal(50, fast);   // 1000ms /  20ms
        }

        [Fact]
        public void Tick_delta_is_the_channel_fixed_step()
        {
            var s = new TickScheduler();
            double captured = 0;
            s.Channel("ai", hz: 10).Add(t => captured = t.DeltaMs);
            for (int i = 0; i < 10; i++) s.Pump(10);      // reach one 100ms step
            Assert.Equal(100.0, captured, 3);
        }

        [Fact]
        public void Higher_priority_channels_tick_first()
        {
            var order = new List<string>();
            var s = new TickScheduler();
            s.Channel("low", hz: 100, priority: 1).Add(_ => order.Add("low"));
            s.Channel("high", hz: 100, priority: 10).Add(_ => order.Add("high"));
            s.Pump(10);   // step 10ms → both fire once, in priority order
            Assert.Equal(new[] { "high", "low" }, order);
        }

        [Fact]
        public void Paused_scheduler_ticks_nothing()
        {
            var s = new TickScheduler { Paused = true };
            int n = 0;
            s.Channel("a", hz: 10).Add(_ => n++);
            for (int i = 0; i < 100; i++) s.Pump(10);
            Assert.Equal(0, n);
        }

        [Fact]
        public void Disabled_channel_is_skipped()
        {
            var s = new TickScheduler();
            int n = 0;
            var ch = s.Channel("a", hz: 10);
            ch.Add(_ => n++);
            ch.Enabled = false;
            for (int i = 0; i < 100; i++) s.Pump(10);
            Assert.Equal(0, n);
        }

        [Fact]
        public void Disposing_the_handle_unregisters()
        {
            var s = new TickScheduler();
            int n = 0;
            var handle = s.Channel("a", hz: 10).Add(_ => n++);
            for (int i = 0; i < 10; i++) s.Pump(10);   // one tick
            Assert.Equal(1, n);
            handle.Dispose();
            for (int i = 0; i < 100; i++) s.Pump(10);  // no more
            Assert.Equal(1, n);
        }

        [Fact]
        public void Backlog_is_capped_to_avoid_spiral()
        {
            var s = new TickScheduler();
            int n = 0;
            var ch = s.Channel("a", hz: 10);           // step 100ms, MaxSubstepsPerPump = 5
            ch.Add(_ => n++);
            s.Pump(10_000);                            // 10s at once → would be 100 ticks
            Assert.Equal(5, n);                        // capped, backlog dropped
        }

        [Fact]
        public void BehaviorTree_is_driven_through_a_channel_via_Bind()
        {
            var ctx = new Box();
            var tree = BehaviorTree<Box>.Build().Do(c => c.N++).Create();

            var s = new TickScheduler();
            s.Channel("ai", hz: 10).Add(tree.Bind(ctx));   // the pain point: all trees ticked from one place
            for (int i = 0; i < 100; i++) s.Pump(10);

            Assert.Equal(10, ctx.N);
        }

        [Fact]
        public void Custom_ITickable_is_driven()
        {
            var t = new CountingTickable();
            var s = new TickScheduler();
            s.Channel("a", hz: 10).Add(t);
            for (int i = 0; i < 100; i++) s.Pump(10);
            Assert.Equal(10, t.Ticks);
        }

        private sealed class CountingTickable : ITickable
        {
            public int Ticks;
            public void Tick(in TickInfo tick) => Ticks++;
        }

        [Fact]
        public void Library_game_loop_systems_implement_the_tick_interfaces()
        {
            Assert.True(typeof(ITickable).IsAssignableFrom(typeof(LocomotionSystem)));
            Assert.True(typeof(ITickable).IsAssignableFrom(typeof(SpawningServer)));
            Assert.True(typeof(IAsyncTickable).IsAssignableFrom(typeof(MobServer)));
        }

        // ---- Auto-subscription via the ambient TickHost.Current ----

        private static GridGeoData Field() =>
            new GridGeoDataBuilder(Vec3.Zero, cellSize: 1f, width: 20, depth: 20)
                .Fill((_, _) => (true, false, 0f)).Build();

        [Fact]
        public void MakeCurrent_sets_and_clears_the_ambient_host()
        {
            var prev = TickHost.Current;
            try
            {
                var s = new TickScheduler();
                Assert.Same(s, s.MakeCurrent());
                Assert.Same(s, TickHost.Current);
                s.Dispose();
                Assert.Null(TickHost.Current);   // cleared on dispose
            }
            finally { TickHost.Current = prev; }
        }

        [Fact]
        public void System_auto_subscribes_to_the_current_host_and_unsubscribes_on_dispose()
        {
            var prev = TickHost.Current;
            try
            {
                var s = new TickScheduler().MakeCurrent();
                var loco = new LocomotionSystem(Field());        // AutoTick default true + host set → subscribes itself
                var ch = s.Channels.FirstOrDefault(c => c.Name == "locomotion");
                Assert.NotNull(ch);
                Assert.Equal(1, ch!.Count);
                loco.Dispose();
                Assert.Equal(0, ch.Count);                       // handle disposed → unregistered
            }
            finally { TickHost.Current = prev; }
        }

        [Fact]
        public void AutoTick_false_ignores_the_host()
        {
            var prev = TickHost.Current;
            try
            {
                var s = new TickScheduler().MakeCurrent();
                var loco = new LocomotionSystem(Field(),
                    new LocomotionOptions { AutoTick = false, UseInternalTimer = false });
                Assert.DoesNotContain(s.Channels, c => c.Name == "locomotion");
                loco.Dispose();
            }
            finally { TickHost.Current = prev; }
        }

        [Fact]
        public void Auto_subscribed_system_is_actually_driven_by_the_host()
        {
            var prev = TickHost.Current;
            try
            {
                var s = new TickScheduler().MakeCurrent();
                var loco = new LocomotionSystem(Field(), new LocomotionOptions { Hz = 10 });
                var mover = loco.CreateMover(new Vec3(1, 0, 1), speed: 5f);
                mover.GoTo(new Vec3(12, 0, 12));
                var start = mover.Position;

                for (int i = 0; i < 100; i++) s.Pump(10);        // 1s through the host → loco ticks → mover advances

                Assert.NotEqual(start, mover.Position);
                loco.Dispose();
            }
            finally { TickHost.Current = prev; }
        }
    }
}
