using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SetNet.Unity
{
    /// <summary>
    /// Marshals work from SetNet's background network threads onto Unity's main thread. SetNet message handlers,
    /// lifecycle callbacks, and RPC/room events run <b>off</b> the main thread, but Unity APIs (Transform,
    /// GameObject, UI, …) may only be touched on the main thread. Queue that work with <see cref="Post"/> from any
    /// thread, then <see cref="Drain"/> it once per frame from a <c>MonoBehaviour.Update()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// // In a MonoBehaviour:
    /// void Update() => MainThreadDispatcher.Shared.Drain();
    ///
    /// // In a SetNet handler (background thread):
    /// public Task HandleAsync(MoveMessage msg)
    /// {
    ///     MainThreadDispatcher.Shared.Post(() => transform.position = new Vector3(msg.X, msg.Y));
    ///     return Task.CompletedTask;
    /// }
    /// </code>
    /// </example>
    public sealed class MainThreadDispatcher
    {
        /// <summary>A ready-to-use shared instance. Drain it from one place (e.g. a single dispatcher MonoBehaviour).</summary>
        public static MainThreadDispatcher Shared { get; } = new MainThreadDispatcher();

        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>Queues an action to run on the next <see cref="Drain"/> (i.e. the next frame). Thread-safe.</summary>
        /// <param name="action">The work to run on the main thread.</param>
        public void Post(Action action)
        {
            if (action != null) _queue.Enqueue(action);
        }

        /// <summary>
        /// Queues an action and returns a task that completes (on the main thread) once it has run — so a
        /// background handler can <c>await</c> main-thread work.
        /// </summary>
        /// <param name="action">The work to run on the main thread.</param>
        /// <returns>A task completing after the action executes on the next drain.</returns>
        public Task PostAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Runs all queued actions. Call once per frame from the main thread (e.g. <c>Update()</c>). A throwing
        /// action is swallowed so one bad callback can't stop the rest.
        /// </summary>
        public void Drain()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* swallow — one bad callback must not break the frame's drain */ }
            }
        }
    }
}
