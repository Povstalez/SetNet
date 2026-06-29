using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Utils
{
    /// <summary>
    /// A per-task scheduler that runs each registered action on its own independent delay loop, driven by an
    /// externally-supplied cancellation token. Unlike <see cref="GameLoopScheduler"/> (one shared loop), each
    /// task here owns a separate loop and is guarded against re-entrancy, making it suited to update-style work
    /// that must not overlap with itself.
    /// </summary>
    /// <remarks>
    /// Each task delays for its interval and then runs, so timing is "interval + execution time" rather than a
    /// fixed cadence. The per-task <c>Busy</c> flag prevents a new run from starting while the previous one is
    /// still in progress (a tick is skipped instead). Lifetime is controlled entirely by the token passed to the
    /// constructor; there is no explicit stop method.
    /// </remarks>
    public class UpdateScheduler
    {
        /// <summary>The set of registered tasks, each running on its own loop once <see cref="Start"/> is called.</summary>
        private readonly List<LoopTask> _tasks = new List<LoopTask>();

        /// <summary>The cancellation token that terminates every task loop when signaled.</summary>
        private readonly CancellationToken _token;

        /// <summary>
        /// Creates the scheduler and binds its lifetime to a cancellation token.
        /// </summary>
        /// <param name="token">When cancelled, all task loops started by <see cref="Start"/> exit.</param>
        public UpdateScheduler(CancellationToken token)
        {
            _token = token;
        }

        /// <summary>
        /// Registers a periodic action to run on its own loop, delaying <paramref name="milliseconds"/> before
        /// each invocation. Returns this instance for fluent chaining of multiple schedules.
        /// </summary>
        /// <param name="milliseconds">The delay before each invocation, in milliseconds.</param>
        /// <param name="action">The asynchronous work to run each time the task fires.</param>
        /// <returns>This <see cref="UpdateScheduler"/>, enabling a fluent <c>.Every(...).Every(...)</c> style.</returns>
        public UpdateScheduler Every(int milliseconds, Func<Task> action)
        {
            _tasks.Add(new LoopTask
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds),
                Action = action,
                Busy = false
            });
            return this;
        }

        /// <summary>
        /// Launches an independent loop for every registered task so they begin ticking concurrently. Call once
        /// after all tasks have been registered via <see cref="Every"/>; the loops run until the constructor's
        /// cancellation token is signaled.
        /// </summary>
        public void Start()
        {
            foreach (var task in _tasks)
            {
                RunLoop(task);
            }
        }

        /// <summary>
        /// The per-task loop body: waits the task's interval, then (if the task is not already running) marks it
        /// busy and runs its action, repeating until cancellation. Declared <c>async void</c> as a fire-and-forget
        /// loop launched by <see cref="Start"/>; expected cancellation exits cleanly, other exceptions are logged
        /// and the loop continues. The <c>Busy</c> guard prevents overlapping executions of the same task.
        /// </summary>
        /// <param name="task">The task whose interval, action, and busy state drive this loop.</param>
        private async void RunLoop(LoopTask task)
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(task.Interval, _token);
                    if (!task.Busy)
                    {
                        task.Busy = true;
                        await task.Action();
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scheduler] Error: {ex.Message}");
                }
                finally
                {
                    task.Busy = false;
                }
            }
        }

        /// <summary>
        /// Internal record of one scheduled task: its delay, its action, and a re-entrancy flag indicating whether
        /// an invocation is currently in flight.
        /// </summary>
        private class LoopTask
        {
            /// <summary>The delay applied before each invocation of <see cref="Action"/>.</summary>
            public TimeSpan Interval;

            /// <summary>The asynchronous work invoked each tick.</summary>
            public Func<Task> Action;

            /// <summary>True while an invocation is running; used to skip a tick instead of overlapping executions.</summary>
            public bool Busy;
        }
    }
}