using System.Threading.Tasks;

namespace SetNet.Ticks
{
    /// <summary>
    /// One tick's timing, handed to everything the tick scheduler drives. Carries the delta both in milliseconds and
    /// seconds (library modules think in ms; Unity thinks in seconds) plus a monotonically increasing frame number.
    /// </summary>
    public readonly struct TickInfo
    {
        /// <summary>Time since this channel's previous tick, in milliseconds (the channel's fixed step).</summary>
        public double DeltaMs { get; }
        /// <summary>The same delta in seconds (Unity-friendly).</summary>
        public float DeltaSeconds { get; }
        /// <summary>The scheduler's frame counter at this tick.</summary>
        public long Frame { get; }

        /// <summary>Creates a tick info from a millisecond delta and frame number.</summary>
        public TickInfo(double deltaMs, long frame)
        {
            DeltaMs = deltaMs;
            DeltaSeconds = (float)(deltaMs / 1000.0);
            Frame = frame;
        }
    }

    /// <summary>
    /// The unifying contract for anything that needs a periodic update. Implement it and register with a
    /// <c>SetNet.Ticks.TickScheduler</c> channel to be driven from one place at a configured rate — instead of each
    /// system running its own timer or being ticked by hand. The library's game-loop systems (mobs, locomotion,
    /// spawning) implement this; wrap stateful ones (behaviour trees, state machines) with their <c>Bind(context)</c>.
    /// </summary>
    public interface ITickable
    {
        /// <summary>Advances by one channel step.</summary>
        void Tick(in TickInfo tick);
    }

    /// <summary>The async counterpart of <see cref="ITickable"/> for systems whose update is asynchronous (e.g. mob AI).</summary>
    public interface IAsyncTickable
    {
        /// <summary>Advances by one channel step, asynchronously.</summary>
        Task TickAsync(TickInfo tick);
    }
}
