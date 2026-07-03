using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SetNet.Core;
using SetNet.GeoData;
using SetNet.PathFinding;

namespace SetNet.Locomotion
{
    /// <summary>Tunables for the locomotion system.</summary>
    public sealed class LocomotionOptions
    {
        /// <summary>How many times per second positions are advanced. Default 10.</summary>
        public int Hz { get; set; } = 10;
        /// <summary>When true (default), an internal timer advances everything; otherwise call <see cref="LocomotionSystem.Update"/> yourself.</summary>
        public bool UseInternalTimer { get; set; } = true;
        /// <summary>
        /// When true (default) and a <c>SetNet.Ticks.TickHost.Current</c> is set, the system auto-subscribes to it (on the
        /// <see cref="TickChannel"/> at <see cref="Hz"/>) instead of running its own timer — so you don't register it by hand.
        /// Set false to always use the internal timer even when a tick host exists.
        /// </summary>
        public bool AutoTick { get; set; } = true;
        /// <summary>The tick-scheduler channel this system auto-subscribes into. Default "locomotion".</summary>
        public string TickChannel { get; set; } = "locomotion";
        /// <summary>Priority of the auto-subscribed channel (higher ticks first). Default 100 (movement before AI).</summary>
        public int TickPriority { get; set; } = 100;
    }

    /// <summary>
    /// One thing that moves — a position, a speed, and (optionally) a route it's walking. Create one via
    /// <see cref="LocomotionSystem.CreateMover"/> and it is <b>automatically</b> part of the unified tick; the system
    /// advances <see cref="Position"/> along the route each tick. It replicates nothing — read <see cref="Position"/>
    /// and send it your own way. Attach your entity via <see cref="Owner"/>.
    /// </summary>
    public sealed class Mover : IDisposable
    {
        private readonly LocomotionSystem _system;
        private PathFollower? _follower;

        /// <summary>Current world position (advanced by the system while moving; you may also set it directly).</summary>
        public Vec3 Position { get; set; }
        /// <summary>Movement speed in world units per second (typically your character's move-speed stat).</summary>
        public float Speed { get; set; }
        /// <summary>Back-reference to whatever owns this mover (a character, a mob…) — the system never touches it.</summary>
        public object? Owner { get; set; }
        /// <summary>The current destination, or null when idle.</summary>
        public Vec3? Destination { get; private set; }
        /// <summary>True while the mover is following a route.</summary>
        public bool IsMoving => _follower is { Arrived: false };
        /// <summary>Raised once when the mover reaches its destination.</summary>
        public event Action<Mover>? DestinationReached;

        internal Mover(LocomotionSystem system, Vec3 start, float speed, object? owner)
        { _system = system; Position = start; Speed = speed; Owner = owner; }

        /// <summary>
        /// Sends the mover to a point: paths there (server-authoritative) and starts following. Fires the system's
        /// <see cref="LocomotionSystem.Started"/> hook so you can replicate just the destination. Returns false (idle)
        /// if unreachable.
        /// </summary>
        public bool GoTo(Vec3 destination)
        {
            var path = _system.Finder.FindPath(Position, destination);
            if (path.IsEmpty) { _follower = null; Destination = null; return false; }
            _follower = new PathFollower(path);
            Destination = destination;
            _system.RaiseStarted(this);      // ← hook: send the point to clients (they re-path locally)
            return true;
        }

        /// <summary>Stops following the route where it is.</summary>
        public void Stop() => _follower = null;

        /// <summary>Instantly repositions and clears the route.</summary>
        public void Warp(Vec3 position) { Position = position; _follower = null; Destination = null; }

        // Advances one tick; returns true if it moved. Called by the system.
        internal bool Step(float dtSeconds)
        {
            if (_follower is null || _follower.Arrived) { _follower = null; return false; }
            if (dtSeconds <= 0f) return false;
            Position = _follower.Step(Position, Speed * dtSeconds);
            if (_follower.Arrived) { _follower = null; DestinationReached?.Invoke(this); }
            return true;
        }

        /// <summary>Removes the mover from the system (automatic un-subscribe). Call when its entity leaves.</summary>
        public void Dispose() => _system.Remove(this);
    }

    /// <summary>
    /// The single, unified movement system: it advances <b>every</b> registered <see cref="Mover"/> along its route at
    /// a fixed rate. Create movers with <see cref="CreateMover"/> (they auto-subscribe) — that is the only wiring.
    /// It sends nothing over the network: replication is yours. Subscribe to <see cref="Started"/> to forward a new
    /// destination to clients (L2-style — the client re-paths from the point locally).
    /// </summary>
    public sealed class LocomotionSystem : IDisposable, SetNet.Ticks.ITickable
    {
        private static readonly ConcurrentDictionary<BaseServer, LocomotionSystem> Servers = new ConcurrentDictionary<BaseServer, LocomotionSystem>();

        private readonly ConcurrentDictionary<Mover, byte> _movers = new ConcurrentDictionary<Mover, byte>();
        private readonly Timer? _timer;
        private readonly IDisposable? _tickReg;
        private int _ticking;

        /// <summary>The shared pathfinder (built once, reused for every mover).</summary>
        internal IPathfinder Finder { get; }

        /// <summary>Raised when a mover gets a new destination (via <see cref="Mover.GoTo"/>) — the moment to send the point to clients.</summary>
        public event Action<Mover>? Started;

        /// <summary>Creates a standalone system over some geometry (fine for headless / tests).</summary>
        public LocomotionSystem(IGeoData geo, LocomotionOptions? options = null)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            var o = options ?? new LocomotionOptions();
            Finder = Pathfinding.For(geo);

            // Prefer the ambient tick host (auto-subscribe, one place drives everything); fall back to the internal timer.
            if (o.AutoTick && SetNet.Ticks.TickHost.Current is { } host)
            {
                _tickReg = host.Register((SetNet.Ticks.ITickable)this, o.TickChannel, o.Hz, o.TickPriority);
            }
            else if (o.UseInternalTimer)
            {
                var period = Math.Max(1, 1000 / Math.Max(1, o.Hz));
                _timer = new Timer(_ => Tick(period / 1000f), null, period, period);
            }
        }

        internal static LocomotionSystem Enable(BaseServer server, IGeoData geo, LocomotionOptions options)
            => Servers.GetOrAdd(server, _ => new LocomotionSystem(geo, options));

        /// <summary>Creates a mover and <b>automatically subscribes</b> it — it is ticking from this moment.</summary>
        public Mover CreateMover(Vec3 start, float speed, object? owner = null)
        {
            var m = new Mover(this, start, speed, owner);
            _movers[m] = 0;
            return m;
        }

        internal void Remove(Mover m) => _movers.TryRemove(m, out _);
        internal void RaiseStarted(Mover m) => Started?.Invoke(m);

        /// <summary>Every live mover.</summary>
        public IReadOnlyCollection<Mover> Movers => (IReadOnlyCollection<Mover>)_movers.Keys;
        /// <summary>How many movers are registered.</summary>
        public int Count => _movers.Count;

        /// <summary>Advances all movers by <paramref name="dtMs"/>. Call this yourself when the internal timer is off.</summary>
        public void Update(double dtMs) => Tick((float)(dtMs / 1000.0));

        /// <summary>Lets a <c>SetNet.Ticks.TickScheduler</c> drive this system: <c>channel.Add(loco)</c>.</summary>
        void SetNet.Ticks.ITickable.Tick(in SetNet.Ticks.TickInfo tick) => Update(tick.DeltaMs);

        private void Tick(float dtSeconds)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;   // never overlap ticks
            try { foreach (var m in _movers.Keys) m.Step(dtSeconds); }
            catch { /* never throw on the timer thread */ }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        /// <summary>Stops the internal timer.</summary>
        public void Dispose() { _tickReg?.Dispose(); _timer?.Dispose(); }
    }

    /// <summary>Attaches the unified locomotion system to a server (one per server).</summary>
    public static class LocomotionExtensions
    {
        /// <summary>Enables the server-wide locomotion system; returns it so you can <c>CreateMover</c> and hook <c>Started</c>.</summary>
        public static LocomotionSystem UseLocomotion(this BaseServer server, IGeoData geo, LocomotionOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            return LocomotionSystem.Enable(server, geo, options ?? new LocomotionOptions());
        }
    }
}
