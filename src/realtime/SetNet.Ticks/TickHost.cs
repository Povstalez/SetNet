using System;

namespace SetNet.Ticks
{
    /// <summary>
    /// A place periodic updates can register themselves into — implemented by <c>SetNet.Ticks.TickScheduler</c>. The
    /// registration seam lives in core so the library's systems can auto-subscribe without depending on the scheduler package.
    /// </summary>
    public interface ITickHost
    {
        /// <summary>Registers a synchronous tickable into a named channel at the given rate; dispose the handle to unregister.</summary>
        IDisposable Register(ITickable tickable, string channel, int hz, int priority = 0);

        /// <summary>Registers an asynchronous tickable into a named channel at the given rate; dispose the handle to unregister.</summary>
        IDisposable Register(IAsyncTickable tickable, string channel, int hz, int priority = 0);
    }

    /// <summary>
    /// The ambient tick host. Set it once at startup — e.g. <c>new TickScheduler().MakeCurrent()</c> — and the library's
    /// game-loop systems (<c>MobServer</c>, <c>LocomotionSystem</c>, <c>SpawningServer</c>) auto-subscribe to it when they
    /// are created, each into its own channel, <b>instead of</b> running its own internal timer. That way you never wire
    /// up each mob/system by hand: create the scheduler first, then your <c>UseXxx(...)</c> calls just work.
    /// <para>Opt a single system out with its <c>AutoTick = false</c> option (it falls back to its own timer).</para>
    /// </summary>
    public static class TickHost
    {
        /// <summary>The current ambient host, or null when none is set (systems then use their own timers, as before).</summary>
        public static ITickHost? Current { get; set; }
    }
}
