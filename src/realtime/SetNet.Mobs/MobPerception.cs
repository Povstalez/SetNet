using System;
using System.Collections.Generic;
using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>
    /// Supplies current player positions to the mob AI. The app knows where players are (e.g. from its own
    /// <c>SetNet.StateSync</c> world or movement system); the mob layer only reads them. Implement this or hand
    /// <see cref="MobOptions.PlayerPosition"/> a lambda.
    /// </summary>
    public interface IPlayerPositions
    {
        /// <summary>The current world position of a player, or null if unknown/offline.</summary>
        Vec3? PositionOf(string playerKey);

        /// <summary>The keys of all players that could be perceived (usually all online players).</summary>
        IEnumerable<string> AllPlayers();
    }

    /// <summary>
    /// Builds a mob's <see cref="MobSenses"/> each tick from the players the app exposes. Pluggable so a large world
    /// can swap the default radius scan for a spatial-grid query. The default is <see cref="RadiusPerception"/>.
    /// </summary>
    public interface IMobPerception
    {
        /// <summary>
        /// Produces the sensed players for <paramref name="mob"/>. <paramref name="positions"/> supplies player
        /// locations; <paramref name="geo"/> (may be null) provides line-of-sight; <paramref name="aggroRadius"/> is
        /// the sense radius; <paramref name="requireLos"/> drops players the mob cannot see.
        /// </summary>
        IReadOnlyList<PerceivedPlayer> Perceive(MobInstance mob, IPlayerPositions positions, IGeoData? geo, float aggroRadius, bool requireLos);
    }

    /// <summary>
    /// The default perception: every player within <c>aggroRadius</c> is sensed; when <c>requireLos</c> is set and a
    /// <see cref="IGeoData"/> is available, players the mob has no line of sight to are dropped. Threat is read from
    /// the mob's own <see cref="ThreatTable"/>.
    /// </summary>
    public sealed class RadiusPerception : IMobPerception
    {
        /// <summary>A shared, stateless instance.</summary>
        public static readonly RadiusPerception Instance = new RadiusPerception();

        /// <inheritdoc/>
        public IReadOnlyList<PerceivedPlayer> Perceive(MobInstance mob, IPlayerPositions positions, IGeoData? geo, float aggroRadius, bool requireLos)
        {
            var result = new List<PerceivedPlayer>();
            if (positions == null) return result;
            var r2 = aggroRadius * aggroRadius;
            foreach (var key in positions.AllPlayers())
            {
                if (string.IsNullOrEmpty(key)) continue;
                var pos = positions.PositionOf(key);
                if (pos == null) continue;
                var p = pos.Value;
                var d2 = Vec3.DistanceSquared(mob.Position, p);
                if (d2 > r2) continue;

                var los = geo == null || geo.LineOfSight(mob.Position, p);
                if (requireLos && !los) continue;

                result.Add(new PerceivedPlayer(key, (float)Math.Sqrt(d2), los, mob.Threat.Of(key), p));
            }
            return result;
        }
    }

    /// <summary>Adapts a <c>Func&lt;string,Vec3?&gt;</c> + an all-players source to <see cref="IPlayerPositions"/>.</summary>
    internal sealed class DelegatePlayerPositions : IPlayerPositions
    {
        private readonly Func<string, Vec3?> _positionOf;
        private readonly Func<IEnumerable<string>> _all;

        public DelegatePlayerPositions(Func<string, Vec3?> positionOf, Func<IEnumerable<string>> all)
        {
            _positionOf = positionOf ?? (_ => null);
            _all = all ?? Array.Empty<string>;
        }

        public Vec3? PositionOf(string playerKey) => _positionOf(playerKey);
        public IEnumerable<string> AllPlayers() => _all();
    }
}
