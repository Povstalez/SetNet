namespace SetNet.StateSync
{
    /// <summary>
    /// One-time state-sync bootstrap. Call <see cref="Enable"/> at startup — before constructing your
    /// <c>BaseClient</c>/<c>BaseServer</c> — so this assembly is loaded and the spawn/despawn/snapshot/ack/input handlers
    /// are discovered. Cheap no-op beyond forcing the load; safe to call more than once. Register your archetype schemas
    /// (<see cref="ReplicaRegistry"/>) too, identically on both ends.
    /// </summary>
    public static class StateSyncRuntime
    {
        /// <summary>Ensures the state-sync layer is discoverable. Call once at startup.</summary>
        public static void Enable()
        {
            _ = StateSyncTypes.Snapshot;
        }
    }
}
