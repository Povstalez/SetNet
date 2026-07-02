namespace SetNet.Mobs
{
    /// <summary>
    /// The replication seam — <b>the</b> extension point that keeps <c>SetNet.Mobs</c> free of any state-replication
    /// dependency. The framework calls these hooks as mobs spawn, move and despawn; an adapter decides how (if at all)
    /// that reaches clients. The default is <see cref="NullMobReplication"/> (no-op): an app without a replication
    /// package can instead poll <see cref="MobServer.Mobs"/> or subscribe to <see cref="MobServer.MobMoved"/> /
    /// <see cref="MobServer.MobUpdated"/>. The <c>SetNet.Mobs.StateSync</c> package supplies a StateSync-backed adapter.
    /// </summary>
    public interface IMobReplication
    {
        /// <summary>Called once when a mob is spawned (create the replicated entity / register it).</summary>
        void OnMobSpawned(MobInstance mob);

        /// <summary>Called each tick with the mob's current state (mutate the replicated entity's fields).</summary>
        void OnMobUpdated(MobInstance mob);

        /// <summary>Called once when a mob is despawned (remove the replicated entity).</summary>
        void OnMobDespawned(string mobId);
    }

    /// <summary>
    /// The default replication seam: does nothing. With it, mob state never leaves the server through Mobs itself —
    /// read <see cref="MobServer.Mobs"/> or handle <see cref="MobServer.MobMoved"/>/<see cref="MobServer.MobUpdated"/>
    /// and replicate however you like, or plug an adapter such as the one in <c>SetNet.Mobs.StateSync</c>.
    /// </summary>
    public sealed class NullMobReplication : IMobReplication
    {
        /// <summary>A shared, stateless instance.</summary>
        public static readonly NullMobReplication Instance = new NullMobReplication();

        /// <inheritdoc/>
        public void OnMobSpawned(MobInstance mob) { }

        /// <inheritdoc/>
        public void OnMobUpdated(MobInstance mob) { }

        /// <inheritdoc/>
        public void OnMobDespawned(string mobId) { }
    }
}
