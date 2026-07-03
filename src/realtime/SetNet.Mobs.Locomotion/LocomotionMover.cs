using System.Collections.Concurrent;
using SetNet.GeoData;
using SetNet.Locomotion;
using SetNet.Mobs;

namespace SetNet.Mobs.Locomotion
{
    /// <summary>
    /// An <see cref="IMobMover"/> that advances mobs through a shared <see cref="LocomotionSystem"/>: every mob gets a
    /// <see cref="Mover"/> in the same unified tick as the rest of your entities. Wire it with
    /// <c>MobOptions.Mover = loco.AsMobMover()</c>. Because each mob is now a <see cref="Mover"/> with the mob as its
    /// <see cref="Mover.Owner"/>, the system's <see cref="LocomotionSystem.Started"/> hook fires for mob movement too —
    /// so you can replicate players and mobs the same way (send just the destination, L2-style).
    /// </summary>
    public sealed class LocomotionMover : IMobMover
    {
        private readonly LocomotionSystem _system;
        private readonly ConcurrentDictionary<string, Mover> _movers = new ConcurrentDictionary<string, Mover>();

        /// <summary>Creates the bridge over a locomotion system.</summary>
        public LocomotionMover(LocomotionSystem system) => _system = system;

        /// <inheritdoc/>
        public void OnSpawn(MobInstance mob, float speed)
            => _movers[mob.Id] = _system.CreateMover(mob.Position, speed, owner: mob);

        /// <inheritdoc/>
        public void SetGoal(MobInstance mob, Vec3 goal)
        {
            if (_movers.TryGetValue(mob.Id, out var m)) m.GoTo(goal);
        }

        /// <inheritdoc/>
        public void Stop(MobInstance mob)
        {
            if (_movers.TryGetValue(mob.Id, out var m)) m.Stop();
        }

        /// <inheritdoc/>
        public Vec3 Position(MobInstance mob)
            => _movers.TryGetValue(mob.Id, out var m) ? m.Position : mob.Position;

        /// <inheritdoc/>
        public void OnDespawn(MobInstance mob)
        {
            if (_movers.TryRemove(mob.Id, out var m)) m.Dispose();
        }
    }

    /// <summary>Bridges <see cref="LocomotionSystem"/> to <see cref="IMobMover"/>.</summary>
    public static class LocomotionMoverExtensions
    {
        /// <summary>Wraps this locomotion system as a mob mover for <c>MobOptions.Mover</c>.</summary>
        public static IMobMover AsMobMover(this LocomotionSystem system) => new LocomotionMover(system);
    }
}
