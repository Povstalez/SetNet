using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Utils
{
    /// <summary>
    /// A single-loop scheduler that runs registered actions at fixed intervals from one shared timing loop,
    /// suitable for server tick updates and other periodic work. Tasks are configured fluently via
    /// <see cref="Every"/>, then the loop is started either blocking (<see cref="StartAsync"/>) or in the
    /// background (<see cref="StartInBackground"/>) and stopped with <see cref="StopAsync"/>.
    /// </summary>
    /// <remarks>
    /// The loop polls every millisecond and dispatches each due task fire-and-forget, so a long-running action
    /// does not stall the loop and a task may overlap with its own previous run. Scheduling is interval-based,
    /// not drift-corrected: each task's next execution is computed from the time it was dispatched. Register all
    /// tasks before starting; the task list is not synchronized for concurrent mutation while the loop runs.
    /// </remarks>
    public class GameLoopScheduler
    {
        /// <summary>The set of registered periodic tasks, each tracking its interval and next-due time.</summary>
        private readonly List<ScheduledTask> _tasks = new List<ScheduledTask>();

        /// <summary>Internal cancellation source signaled by <see cref="StopAsync"/> to end the loop.</summary>
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Handle to the running loop, retained so <see cref="StopAsync"/> can await its completion.</summary>
        private Task _loopTask;

        /// <summary>
        /// An additional externally-supplied cancellation token that, together with the internal source, can stop
        /// the loop. Currently a non-cancelable default; exposed so a caller's token can participate in shutdown.
        /// </summary>
        public CancellationToken ExternalToken { get; } = new CancellationToken();

        /// <summary>
        /// A cancellation token that is triggered when either the internal stop signal or
        /// <see cref="ExternalToken"/> fires. The loop checks this so both shutdown paths take effect.
        /// </summary>
        /// <remarks>
        /// Each access creates a fresh linked token source; the resulting source is not disposed here, so the
        /// property is intended for use within the loop rather than tight, high-frequency polling.
        /// </remarks>
        public CancellationToken CombinedToken => CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ExternalToken).Token;

        /// <summary>
        /// Registers a periodic action to be invoked approximately every <paramref name="milliseconds"/> once the
        /// loop is running. The first execution is due immediately (next-execution is seeded to now). Returns this
        /// instance to allow fluent chaining of multiple schedules.
        /// </summary>
        /// <param name="milliseconds">The interval between successive invocations, in milliseconds.</param>
        /// <param name="action">The asynchronous work to run on each tick.</param>
        /// <returns>This <see cref="GameLoopScheduler"/>, enabling a fluent <c>.Every(...).Every(...)</c> style.</returns>
        public GameLoopScheduler Every(int milliseconds, Func<Task> action)
        {
            _tasks.Add(new ScheduledTask
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds),
                Action = action,
                NextExecution = DateTime.UtcNow
            });
            return this;
        }

        /// <summary>
        /// Starts the scheduling loop and awaits it, blocking the caller until the loop is cancelled via
        /// <see cref="StopAsync"/> or <see cref="ExternalToken"/>. Use this when the scheduler should own the
        /// current execution flow (for example, as the main loop of a dedicated server process).
        /// </summary>
        /// <returns>A task that completes when the loop exits after cancellation.</returns>
        public async Task StartAsync()
        {
            _loopTask = RunLoopAsync();
            await _loopTask;
        }

        /// <summary>
        /// Starts the scheduling loop without awaiting it, so it runs concurrently while the caller continues.
        /// Use this when the scheduler should tick alongside other work; stop it later with <see cref="StopAsync"/>.
        /// </summary>
        public void StartInBackground()
        {
            _loopTask = RunLoopAsync();
        }

        /// <summary>
        /// Signals cancellation and waits for the running loop to finish, providing an orderly shutdown. The
        /// expected <see cref="TaskCanceledException"/> from cancellation is swallowed so stopping is exception-free.
        /// </summary>
        /// <returns>A task that completes once the loop has fully stopped (or immediately if it was never started).</returns>
        public async Task StopAsync()
        {
            _cts.Cancel();
            if (_loopTask != null)
            {
                try
                {
                    await _loopTask;
                }
                catch (TaskCanceledException) { }
            }
        }

        /// <summary>
        /// The core timing loop: repeatedly checks every registered task and, for any that is due, dispatches it
        /// fire-and-forget and advances its next-execution time, then yields for 1 ms before the next pass. Runs
        /// until <see cref="CombinedToken"/> is cancelled.
        /// </summary>
        /// <returns>A task representing the running loop; completes when cancellation is observed.</returns>
        /// <remarks>
        /// Because due tasks are started with <c>_ = RunTaskSafely(task)</c> (not awaited), the loop never blocks
        /// on a slow task, but consecutive runs of the same task can overlap if it takes longer than its interval.
        /// </remarks>
        private async Task RunLoopAsync()
        {
            while (!CombinedToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                foreach (var task in _tasks)
                {
                    if (now >= task.NextExecution)
                    {
                        _ = RunTaskSafely(task);
                        task.NextExecution = now + task.Interval;
                    }
                }

                await Task.Delay(1, CombinedToken);
            }
        }

        /// <summary>
        /// Invokes a single scheduled task's action and contains any exception it throws, logging it to the
        /// console so one failing task cannot crash the loop or be lost as an unobserved task exception.
        /// </summary>
        /// <param name="task">The scheduled task whose action should be executed.</param>
        /// <returns>A task that completes when the action finishes or its exception has been handled.</returns>
        private async Task RunTaskSafely(ScheduledTask task)
        {
            try
            {
                await task.Action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameLoop] Error in scheduled task: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal record of one registered periodic task: how often it runs, what it does, and when it is next due.
        /// </summary>
        private class ScheduledTask
        {
            /// <summary>The desired delay between successive executions.</summary>
            public TimeSpan Interval;

            /// <summary>The asynchronous work invoked on each tick.</summary>
            public Func<Task> Action;

            /// <summary>The UTC timestamp at or after which the task should next be dispatched.</summary>
            public DateTime NextExecution;
        }
    }
}