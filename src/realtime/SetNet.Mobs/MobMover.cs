using SetNet.GeoData;

namespace SetNet.Mobs
{
    /// <summary>
    /// Optional seam that takes over a mob's <b>movement</b> (position stepping) from the built-in path-follower, so
    /// mobs can be advanced by an external, unified system (e.g. <c>SetNet.Mobs.Locomotion</c> over
    /// <c>SetNet.Locomotion</c>). Set it via <see cref="MobOptions.Mover"/>; leave it null to keep the built-in
    /// movement. The AI, perception, threat, combat and replication are unchanged — only where the position advances
    /// moves out.
    /// </summary>
    public interface IMobMover
    {
        /// <summary>A mob spawned — start tracking it at its position with the given move speed.</summary>
        void OnSpawn(MobInstance mob, float speed);

        /// <summary>The brain wants the mob to move to <paramref name="goal"/> (called when the destination changes).</summary>
        void SetGoal(MobInstance mob, Vec3 goal);

        /// <summary>The mob should stop where it is (its move intent was cleared).</summary>
        void Stop(MobInstance mob);

        /// <summary>The mob's current position (advanced externally between calls).</summary>
        Vec3 Position(MobInstance mob);

        /// <summary>A mob was removed (died / despawned) — stop tracking it.</summary>
        void OnDespawn(MobInstance mob);
    }
}
