using System;
using System.Collections.Generic;
using SetNet.Core;

namespace SetNet.StateSync
{
    /// <summary>
    /// Decides which entities a given observer (peer) should see this tick. Entities that enter an observer's set are
    /// spawned on that client; entities that leave are despawned. Implement this to scope replication (area-of-interest)
    /// so a client only receives nearby/relevant entities instead of the whole world.
    /// </summary>
    public interface IInterestManager
    {
        /// <summary>Returns the entities visible to <paramref name="observer"/> from the full set of live entities.</summary>
        IEnumerable<NetworkEntity> Query(BasePeer observer, IReadOnlyCollection<NetworkEntity> all);
    }

    /// <summary>The trivial interest manager: every observer sees every entity. The default; fine for small worlds.</summary>
    public sealed class AllInterest : IInterestManager
    {
        /// <inheritdoc/>
        public IEnumerable<NetworkEntity> Query(BasePeer observer, IReadOnlyCollection<NetworkEntity> all) => all;
    }

    /// <summary>
    /// Distance-based interest: an observer sees entities within <see cref="Radius"/> of its focus point, plus (optionally)
    /// the entities it owns regardless of distance. You supply how to read an entity's position and an observer's focus
    /// position, since the core doesn't know which field is "position".
    /// </summary>
    public sealed class DistanceInterest : IInterestManager
    {
        private readonly Func<NetworkEntity, Vec3> _entityPosition;
        private readonly Func<BasePeer, Vec3> _observerPosition;
        private readonly bool _alwaysSeeOwned;

        /// <summary>The visibility radius (world units).</summary>
        public float Radius { get; }

        /// <summary>Creates a distance interest manager.</summary>
        /// <param name="entityPosition">Reads an entity's world position (e.g. its position field).</param>
        /// <param name="observerPosition">Reads an observer's focus/camera position.</param>
        /// <param name="radius">Visibility radius in world units.</param>
        /// <param name="alwaysSeeOwnedEntities">When true, an observer always sees entities it owns, even outside the radius.</param>
        public DistanceInterest(Func<NetworkEntity, Vec3> entityPosition, Func<BasePeer, Vec3> observerPosition,
            float radius, bool alwaysSeeOwnedEntities = true)
        {
            _entityPosition = entityPosition ?? throw new ArgumentNullException(nameof(entityPosition));
            _observerPosition = observerPosition ?? throw new ArgumentNullException(nameof(observerPosition));
            Radius = radius;
            _alwaysSeeOwned = alwaysSeeOwnedEntities;
        }

        /// <inheritdoc/>
        public IEnumerable<NetworkEntity> Query(BasePeer observer, IReadOnlyCollection<NetworkEntity> all)
        {
            var focus = _observerPosition(observer);
            var r2 = Radius * Radius;
            var ownerId = observer.CurrentPeerInfo.Id;
            foreach (var e in all)
            {
                if (_alwaysSeeOwned && e.Owner == ownerId) { yield return e; continue; }
                if (Vec3.DistanceSquared(_entityPosition(e), focus) <= r2) yield return e;
            }
        }
    }
}
