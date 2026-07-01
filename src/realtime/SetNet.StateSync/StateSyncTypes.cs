namespace SetNet.StateSync
{
    /// <summary>
    /// Reserved wire type ids for the state-sync protocol. These sit below the matchmaking range (65523/65524/65525)
    /// so state replication composes with rooms/matchmaking/auth/rpc without collisions. Don't reuse these ids.
    /// </summary>
    public static class StateSyncTypes
    {
        /// <summary>Server → client: an entity was created in this client's view (reliable).</summary>
        public const ushort Spawn = ushort.MaxValue - 17;      // 65518

        /// <summary>Server → client: an entity was removed from this client's view (reliable).</summary>
        public const ushort Despawn = ushort.MaxValue - 16;    // 65519

        /// <summary>Server → client: a world snapshot (delta-compressed, unreliable).</summary>
        public const ushort Snapshot = ushort.MaxValue - 15;   // 65520

        /// <summary>Client → server: acknowledgement of the latest applied snapshot tick (unreliable).</summary>
        public const ushort Ack = ushort.MaxValue - 14;        // 65521

        /// <summary>Client → server: an input command for the client's owned entity (unreliable).</summary>
        public const ushort Input = ushort.MaxValue - 13;      // 65522
    }

    /// <summary>The wire/interpolation type of a replicated field. The core interpolates numeric and vector/quaternion fields; others snap.</summary>
    public enum FieldType : byte
    {
        /// <summary>A boolean flag (snaps).</summary>
        Bool = 0,
        /// <summary>A single unsigned byte (snaps).</summary>
        Byte = 1,
        /// <summary>A 32-bit signed integer (snaps).</summary>
        Int = 2,
        /// <summary>A 32-bit unsigned integer (snaps).</summary>
        UInt = 3,
        /// <summary>A 64-bit signed integer (snaps).</summary>
        Long = 4,
        /// <summary>A 32-bit float (interpolatable).</summary>
        Float = 5,
        /// <summary>A 64-bit double (interpolatable).</summary>
        Double = 6,
        /// <summary>A UTF-8 string (snaps).</summary>
        String = 7,
        /// <summary>A 2D vector (interpolatable).</summary>
        Vector2 = 8,
        /// <summary>A 3D vector (interpolatable).</summary>
        Vector3 = 9,
        /// <summary>A quaternion rotation (interpolatable via nlerp).</summary>
        Quaternion = 10,
    }

    /// <summary>Tunables for state replication. Defaults suit a typical fast-paced game (30 Hz, ~100 ms interpolation buffer).</summary>
    public sealed class StateSyncOptions
    {
        /// <summary>How many world snapshots the server sends per second. Default 30.</summary>
        public int TickRate { get; set; } = 30;

        /// <summary>How far behind the newest snapshot the client renders, in milliseconds, to smooth over jitter/loss. Default 100.</summary>
        public int InterpolationDelayMs { get; set; } = 100;

        /// <summary>How many recent snapshot ticks each side keeps for delta baselines. Default 32.</summary>
        public int MaxSnapshotHistory { get; set; } = 32;

        /// <summary>The interest manager deciding which entities each client sees. Default: everyone sees everything.</summary>
        public IInterestManager Interest { get; set; } = new AllInterest();

        /// <summary>
        /// (Server) When true, every peer that connects automatically becomes an observer (starts receiving the world).
        /// Set false to control this yourself — call <c>world.AddObserver(peer)</c> once a player is actually ready
        /// (e.g. after auth or after joining a room), so you don't replicate the game to peers still in the lobby. Default true.
        /// </summary>
        public bool AutoObserve { get; set; } = true;
    }
}
