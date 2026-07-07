using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using SetNet.GeoData;
using SetNet.Hitscan;
using SetNet.Ticks;

namespace SetNet.Projectiles
{
    /// <summary>One in-flight projectile. Server-authoritative; you read its <see cref="Position"/> and replicate however you like.</summary>
    public sealed class Projectile
    {
        /// <summary>Unique id.</summary>
        public string Id { get; internal set; } = "";
        /// <summary>Who fired it (never hit by it).</summary>
        public string OwnerId { get; internal set; } = "";
        /// <summary>Owner faction (passed to the hit detector for your friendly-fire rules).</summary>
        public string? Faction { get; internal set; }
        /// <summary>Current position (advanced each tick).</summary>
        public Vec3 Position { get; internal set; }
        /// <summary>Current velocity (world units/second; changes under gravity).</summary>
        public Vec3 Velocity { get; internal set; }
        /// <summary>Distance travelled so far.</summary>
        public float Traveled { get; internal set; }
        /// <summary>Max distance before it expires (0 = unlimited).</summary>
        public float MaxDistance { get; internal set; }
        /// <summary>Max lifetime in ms before it expires (0 = unlimited).</summary>
        public double LifetimeMs { get; internal set; }
        /// <summary>Age in ms.</summary>
        public double AgeMs { get; internal set; }
        /// <summary>Still flying?</summary>
        public bool Alive { get; internal set; } = true;
        /// <summary>Your payload (weapon/damage info…).</summary>
        public object? Tag { get; set; }
    }

    /// <summary>Parameters for spawning a projectile.</summary>
    public sealed class ProjectileSpawn
    {
        /// <summary>Start position (muzzle).</summary>
        public Vec3 Origin { get; set; }
        /// <summary>Direction of travel (normalized internally).</summary>
        public Vec3 Direction { get; set; }
        /// <summary>Speed in world units/second.</summary>
        public float Speed { get; set; } = 30f;
        /// <summary>Who fired it.</summary>
        public string OwnerId { get; set; } = "";
        /// <summary>Owner faction (optional).</summary>
        public string? Faction { get; set; }
        /// <summary>Max travel distance (0 = unlimited).</summary>
        public float MaxDistance { get; set; }
        /// <summary>Max lifetime in ms (0 = unlimited).</summary>
        public double LifetimeMs { get; set; } = 5000;
        /// <summary>Your payload.</summary>
        public object? Tag { get; set; }
    }

    /// <summary>Options for <see cref="ProjectileSystem"/>.</summary>
    public sealed class ProjectileOptions
    {
        /// <summary>Constant acceleration (e.g. <c>new Vec3(0,-9.81f,0)</c> for gravity; default none = straight line).</summary>
        public Vec3 Gravity { get; set; } = Vec3.Zero;
        /// <summary>Simulation rate when driven by the tick scheduler or the internal timer. Default 30.</summary>
        public int Hz { get; set; } = 30;
        /// <summary>When true (default) and a <c>SetNet.Ticks.TickHost.Current</c> is set, the system auto-subscribes to it.</summary>
        public bool AutoTick { get; set; } = true;
        /// <summary>Tick-scheduler channel this system auto-subscribes into. Default "projectiles".</summary>
        public string TickChannel { get; set; } = "projectiles";
        /// <summary>Priority of the auto-subscribed channel. Default 20.</summary>
        public int TickPriority { get; set; } = 20;
        /// <summary>When no tick host is present, run an internal timer at <see cref="Hz"/>. Default false (you call <c>Update</c>).</summary>
        public bool UseInternalTimer { get; set; }
    }

    /// <summary>
    /// Advances travelling projectiles and resolves their impacts through a pluggable <see cref="IHitDetector"/> — the
    /// <b>same</b> hit seam as <c>SetNet.Hitscan</c>, so your collision code is reused. Each step sweeps the segment from
    /// the old to the new position (no tunnelling through thin targets). It <b>replicates nothing</b> — read
    /// <see cref="Projectiles"/> / handle <see cref="Hit"/> and send your own way. Auto-subscribes to <c>SetNet.Ticks</c>.
    /// </summary>
    public sealed class ProjectileSystem : IDisposable, SetNet.Ticks.ITickable
    {
        private readonly IHitDetector _detector;
        private readonly ProjectileOptions _options;
        private readonly ConcurrentDictionary<string, Projectile> _projectiles = new ConcurrentDictionary<string, Projectile>();
        private readonly IDisposable? _tickReg;
        private readonly Timer? _timer;
        private long _nextId;
        private int _ticking;

        /// <summary>Raised when a projectile is spawned.</summary>
        public event Action<Projectile>? Spawned;
        /// <summary>Raised when a projectile hits something (it is removed right after).</summary>
        public event Action<Projectile, HitResult>? Hit;
        /// <summary>Raised when a projectile expires (range/lifetime) without hitting.</summary>
        public event Action<Projectile>? Expired;

        /// <summary>Creates the system over a hit detector.</summary>
        public ProjectileSystem(IHitDetector detector, ProjectileOptions? options = null)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _options = options ?? new ProjectileOptions();

            if (_options.AutoTick && TickHost.Current is { } host)
            {
                _tickReg = host.Register((SetNet.Ticks.ITickable)this, _options.TickChannel, _options.Hz, _options.TickPriority);
            }
            else if (_options.UseInternalTimer)
            {
                var period = Math.Max(1, 1000 / Math.Max(1, _options.Hz));
                _timer = new Timer(_ => Update(period), null, period, period);
            }
        }

        /// <summary>Every live projectile (snapshot).</summary>
        public IReadOnlyCollection<Projectile> Projectiles => new List<Projectile>(_projectiles.Values);
        /// <summary>How many projectiles are in flight.</summary>
        public int Count => _projectiles.Count;

        /// <summary>Spawns a projectile.</summary>
        public Projectile Spawn(ProjectileSpawn spawn)
        {
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            var dir = spawn.Direction.LengthSquared > 1e-12f ? spawn.Direction.Normalized : new Vec3(0, 0, 1);
            var p = new Projectile
            {
                Id = "p" + Interlocked.Increment(ref _nextId),
                OwnerId = spawn.OwnerId,
                Faction = spawn.Faction,
                Position = spawn.Origin,
                Velocity = dir * spawn.Speed,
                MaxDistance = spawn.MaxDistance,
                LifetimeMs = spawn.LifetimeMs,
                Tag = spawn.Tag,
            };
            _projectiles[p.Id] = p;
            Spawned?.Invoke(p);
            return p;
        }

        /// <summary>Removes a projectile early (e.g. despawn on owner death). Returns true if it was live.</summary>
        public bool Remove(string projectileId) => _projectiles.TryRemove(projectileId, out _);

        /// <inheritdoc/>
        void SetNet.Ticks.ITickable.Tick(in SetNet.Ticks.TickInfo tick) => Update(tick.DeltaMs);

        /// <summary>Advances all projectiles by <paramref name="dtMs"/> and resolves impacts. Call this yourself when not tick-driven.</summary>
        public void Update(double dtMs)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;    // never overlap
            try
            {
                var dt = (float)(dtMs / 1000.0);
                if (dt <= 0) return;

                foreach (var p in _projectiles.Values)
                {
                    if (!p.Alive) { _projectiles.TryRemove(p.Id, out _); continue; }

                    if (_options.Gravity.LengthSquared > 0) p.Velocity += _options.Gravity * dt;

                    var next = p.Position + p.Velocity * dt;
                    var delta = next - p.Position;
                    var segLen = delta.Length;

                    if (segLen > 1e-6f)
                    {
                        var ray = new Ray(p.Position, delta);
                        var query = new HitQuery(p.OwnerId, segLen, p.Faction, p.Tag);
                        var hit = _detector.Raycast(in ray, in query);
                        if (hit.Hit)
                        {
                            p.Position = hit.Point;
                            p.Traveled += hit.Distance;
                            p.Alive = false;
                            _projectiles.TryRemove(p.Id, out _);
                            try { Hit?.Invoke(p, hit); } catch { /* one handler must not stall the loop */ }
                            continue;
                        }
                        p.Position = next;
                        p.Traveled += segLen;
                    }

                    p.AgeMs += dtMs;
                    if ((p.MaxDistance > 0 && p.Traveled >= p.MaxDistance) ||
                        (p.LifetimeMs > 0 && p.AgeMs >= p.LifetimeMs))
                    {
                        p.Alive = false;
                        _projectiles.TryRemove(p.Id, out _);
                        try { Expired?.Invoke(p); } catch { /* never throw on the tick */ }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        /// <summary>Stops the internal timer / unsubscribes from the tick scheduler.</summary>
        public void Dispose()
        {
            _tickReg?.Dispose();
            _timer?.Dispose();
        }
    }
}
