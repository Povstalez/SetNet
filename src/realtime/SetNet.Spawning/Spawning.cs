using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.GeoData;
using SetNet.Mobs;

namespace SetNet.Spawning
{
    /// <summary>An XZ area a zone spawns mobs within. Ships box and circle shapes; implement your own for anything else.</summary>
    public abstract class SpawnArea
    {
        /// <summary>The area's centre (used as a fallback spawn point).</summary>
        public abstract Vec3 Center { get; }
        /// <summary>Returns a random point inside the area.</summary>
        public abstract Vec3 RandomPoint(Random rng);

        /// <summary>An axis-aligned box area.</summary>
        public static SpawnArea Box(Vec3 min, Vec3 max) => new BoxArea(min, max);
        /// <summary>A circular area on the XZ plane at a fixed height.</summary>
        public static SpawnArea Circle(Vec3 center, float radius) => new CircleArea(center, radius);

        private sealed class BoxArea : SpawnArea
        {
            private readonly Vec3 _min, _max;
            public BoxArea(Vec3 min, Vec3 max) { _min = min; _max = max; }
            public override Vec3 Center => (_min + _max) * 0.5f;
            public override Vec3 RandomPoint(Random rng) => new Vec3(
                _min.X + (float)rng.NextDouble() * (_max.X - _min.X),
                _min.Y + (float)rng.NextDouble() * (_max.Y - _min.Y),
                _min.Z + (float)rng.NextDouble() * (_max.Z - _min.Z));
        }

        private sealed class CircleArea : SpawnArea
        {
            private readonly Vec3 _center; private readonly float _radius;
            public CircleArea(Vec3 center, float radius) { _center = center; _radius = radius; }
            public override Vec3 Center => _center;
            public override Vec3 RandomPoint(Random rng)
            {
                var angle = rng.NextDouble() * Math.PI * 2;
                var r = _radius * Math.Sqrt(rng.NextDouble());   // uniform over the disc
                return new Vec3(_center.X + (float)(Math.Cos(angle) * r), _center.Y, _center.Z + (float)(Math.Sin(angle) * r));
            }
        }
    }

    /// <summary>One kind of mob to keep alive in a zone: its type, how many, and how long after a death it respawns.</summary>
    public sealed class SpawnEntry
    {
        /// <summary>The registered mob type (matches an <c>IMobBrain.MobType</c>).</summary>
        public string MobType { get; set; } = "";
        /// <summary>How many of this type to keep alive in the zone.</summary>
        public int Count { get; set; } = 1;
        /// <summary>Milliseconds after one dies before it respawns.</summary>
        public int RespawnMs { get; set; } = 5000;
        /// <summary>Spawn health.</summary>
        public double Health { get; set; } = 100;
        /// <summary>Spawn faction.</summary>
        public string Faction { get; set; } = "hostile";
    }

    /// <summary>A named spawn zone: an area plus the mob populations it maintains. Add entries fluently.</summary>
    public sealed class SpawnZone
    {
        /// <summary>Zone id (also set as each spawned mob's <c>Zone</c>).</summary>
        public string Id { get; }
        /// <summary>Where in the world mobs spawn.</summary>
        public SpawnArea Area { get; }
        /// <summary>The populations this zone maintains.</summary>
        public List<SpawnEntry> Entries { get; } = new List<SpawnEntry>();

        /// <summary>Creates a zone over an area.</summary>
        public SpawnZone(string id, SpawnArea area) { Id = id; Area = area; }

        /// <summary>Creates a circular zone.</summary>
        public static SpawnZone Circle(string id, Vec3 center, float radius) => new SpawnZone(id, SpawnArea.Circle(center, radius));
        /// <summary>Creates a box zone.</summary>
        public static SpawnZone Box(string id, Vec3 min, Vec3 max) => new SpawnZone(id, SpawnArea.Box(min, max));

        /// <summary>Adds a mob population to keep alive.</summary>
        public SpawnZone Add(string mobType, int count, int respawnMs = 5000, double health = 100, string faction = "hostile")
        {
            Entries.Add(new SpawnEntry { MobType = mobType, Count = count, RespawnMs = respawnMs, Health = health, Faction = faction });
            return this;
        }
    }

    /// <summary>Settings for the spawning server.</summary>
    public sealed class SpawnOptions
    {
        /// <summary>Optional geometry — spawn points are snapped to the nearest walkable ground when set.</summary>
        public IGeoData? GeoData { get; set; }
        /// <summary>When true (default), an internal timer drives spawning; otherwise call <see cref="SpawningServer.Update"/> yourself.</summary>
        public bool UseInternalTimer { get; set; } = true;
        /// <summary>The internal timer interval (ms).</summary>
        public int TickIntervalMs { get; set; } = 1000;
        /// <summary>RNG seed (0 = time-independent default).</summary>
        public int Seed { get; set; } = 0;
        /// <summary>
        /// When true (default) and a <c>SetNet.Ticks.TickHost.Current</c> is set, spawning auto-subscribes to it (on the
        /// <see cref="TickChannel"/> at a rate derived from <see cref="TickIntervalMs"/>) instead of running its own timer.
        /// Set false to always use the internal timer even when a tick host exists.
        /// </summary>
        public bool AutoTick { get; set; } = true;
        /// <summary>The tick-scheduler channel spawning auto-subscribes into. Default "spawning".</summary>
        public string TickChannel { get; set; } = "spawning";
        /// <summary>Priority of the auto-subscribed channel (higher ticks first). Default 10 (low-frequency housekeeping).</summary>
        public int TickPriority { get; set; } = 10;
    }

    /// <summary>
    /// Keeps spawn zones populated on top of <see cref="MobServer"/>. Each tick it counts living mobs per zone
    /// population, schedules a respawn for any that died, and spawns replacements when their delay elapses. Spawns with
    /// the mob's own respawn disabled so this server owns the respawn timing. Drive it with <see cref="Update"/> (headless)
    /// or let the internal timer run.
    /// </summary>
    public sealed class SpawningServer : IDisposable, SetNet.Ticks.ITickable
    {
        private readonly MobServer _mobs;
        private readonly SpawnOptions _options;
        private readonly Random _rng;
        private readonly List<ZoneRuntime> _zones = new List<ZoneRuntime>();
        private readonly Timer? _timer;
        private readonly IDisposable? _tickReg;
        private long _nowMs;
        private int _ticking;

        internal SpawningServer(MobServer mobs, SpawnOptions options)
        {
            _mobs = mobs; _options = options;
            _rng = options.Seed != 0 ? new Random(options.Seed) : new Random();

            // Prefer the ambient tick host (auto-subscribe, one place drives everything); fall back to the internal timer.
            if (options.AutoTick && SetNet.Ticks.TickHost.Current is { } host)
            {
                var hz = Math.Max(1, (int)Math.Round(1000.0 / Math.Max(1, options.TickIntervalMs)));
                _tickReg = host.Register((SetNet.Ticks.ITickable)this, options.TickChannel, hz, options.TickPriority);
            }
            else if (options.UseInternalTimer)
            {
                _timer = new Timer(_ => Update(options.TickIntervalMs), null, options.TickIntervalMs, options.TickIntervalMs);
            }
        }

        /// <summary>Registers a zone (its populations start filling on the next tick).</summary>
        public SpawningServer AddZone(SpawnZone zone)
        {
            _zones.Add(new ZoneRuntime(zone));
            return this;
        }

        /// <summary>Lets a <c>SetNet.Ticks.TickScheduler</c> drive spawning: <c>channel.Add(spawning)</c>.</summary>
        void SetNet.Ticks.ITickable.Tick(in SetNet.Ticks.TickInfo tick) => Update(tick.DeltaMs);

        /// <summary>Advances spawning by <paramref name="dtMs"/>: fills zones, detects deaths, respawns on schedule.</summary>
        public void Update(double dtMs)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try
            {
                _nowMs += (long)dtMs;

                // Snapshot the set of living mob ids once.
                var alive = new HashSet<string>();
                foreach (var m in _mobs.Mobs) if (m.IsAlive) alive.Add(m.Id);

                foreach (var zone in _zones)
                    foreach (var pop in zone.Populations)
                    {
                        // Remove dead ids and schedule their respawn.
                        for (var i = pop.LiveIds.Count - 1; i >= 0; i--)
                        {
                            if (!alive.Contains(pop.LiveIds[i]))
                            {
                                pop.LiveIds.RemoveAt(i);
                                pop.RespawnDueMs.Add(_nowMs + pop.Entry.RespawnMs);
                            }
                        }

                        // Initial fill: queue immediate spawns for any shortfall not already scheduled.
                        var missing = pop.Entry.Count - pop.LiveIds.Count - pop.RespawnDueMs.Count;
                        for (var i = 0; i < missing; i++) pop.RespawnDueMs.Add(_nowMs);

                        // Fire any due respawns (including the immediate fill above).
                        for (var i = pop.RespawnDueMs.Count - 1; i >= 0; i--)
                        {
                            if (_nowMs >= pop.RespawnDueMs[i] && pop.LiveIds.Count < pop.Entry.Count)
                            {
                                pop.RespawnDueMs.RemoveAt(i);
                                pop.LiveIds.Add(Spawn(zone.Zone, pop.Entry));
                            }
                        }
                    }
            }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        private string Spawn(SpawnZone zone, SpawnEntry entry)
        {
            var pt = zone.Area.RandomPoint(_rng);
            if (_options.GeoData != null) pt = _options.GeoData.SampleNearestWalkable(pt);
            return _mobs.Spawn(new MobSpawn
            {
                Type = entry.MobType,
                Position = pt,
                Zone = zone.Id,
                Health = entry.Health,
                Faction = entry.Faction,
                RespawnMs = 0,   // this server owns respawn timing
            });
        }

        /// <summary>Stops the internal timer.</summary>
        public void Dispose() { _tickReg?.Dispose(); _timer?.Dispose(); }

        private sealed class ZoneRuntime
        {
            public readonly SpawnZone Zone;
            public readonly List<Population> Populations = new List<Population>();
            public ZoneRuntime(SpawnZone zone)
            {
                Zone = zone;
                foreach (var e in zone.Entries) Populations.Add(new Population(e));
            }
        }

        private sealed class Population
        {
            public readonly SpawnEntry Entry;
            public readonly List<string> LiveIds = new List<string>();
            public readonly List<long> RespawnDueMs = new List<long>();
            public Population(SpawnEntry entry) => Entry = entry;
        }
    }

    /// <summary>Attaches the spawning server to a server (over its mob hub).</summary>
    public static class SpawningServerExtensions
    {
        /// <summary>Enables zone-based spawning over an existing <see cref="MobServer"/>; returns it so you can <c>AddZone</c>.</summary>
        public static SpawningServer UseSpawning(this SetNet.Core.BaseServer server, MobServer mobs, SpawnOptions? options = null)
        {
            if (mobs == null) throw new ArgumentNullException(nameof(mobs));
            return new SpawningServer(mobs, options ?? new SpawnOptions());
        }
    }

    /// <summary>No-op bootstrap for symmetry with other modules.</summary>
    public static class SpawningRuntime
    {
        /// <summary>No-op (spawning has no discovered handlers).</summary>
        public static void Enable() { }
    }
}
