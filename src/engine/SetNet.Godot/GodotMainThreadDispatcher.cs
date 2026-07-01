using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SetNet.Godot
{
    /// <summary>
    /// Marshals work from SetNet's background receive/handler threads onto Godot's main thread. SetNet message handlers run
    /// off-thread, but Godot's scene tree (nodes, transforms, signals) must only be touched on the main thread — so enqueue
    /// with <see cref="Post"/> from a handler and call <see cref="Drain"/> once per frame from a Node's <c>_Process</c>.
    /// </summary>
    public sealed class GodotMainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>A shared, process-wide dispatcher for convenience.</summary>
        public static GodotMainThreadDispatcher Shared { get; } = new GodotMainThreadDispatcher();

        /// <summary>Queues an action to run on the next <see cref="Drain"/> (i.e. the main thread).</summary>
        public void Post(Action action) { if (action != null) _queue.Enqueue(action); }

        /// <summary>Queues an action and returns a task that completes (or faults) once it has run on the main thread.</summary>
        public Task PostAsync(Action action)
        {
            if (action == null) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>Runs all queued actions. Call once per frame from a Node's <c>_Process(double delta)</c>.</summary>
        public void Drain()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); } catch { /* isolate one bad callback from the rest */ }
            }
        }
    }
}
