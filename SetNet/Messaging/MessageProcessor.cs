using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SetNet.Messaging
{
    /// <summary>
    /// Routes incoming, already-unframed message payloads to the handler registered for their type identifier.
    /// It is the dispatch core of the messaging layer: the transport hands it a (type, payload) pair and it
    /// invokes the matching async or synchronous handler, observing any faults so handler exceptions surface
    /// through <see cref="OnHandlerError"/> instead of being silently swallowed.
    /// </summary>
    /// <remarks>
    /// Registration is not synchronized and is expected to happen during setup before messages start flowing.
    /// Async handlers are launched fire-and-forget via <see cref="ObserveAsync"/>, so their completion does not
    /// block <see cref="ProcessMessage"/> and ordering across separate messages is not guaranteed.
    /// </remarks>
    public class MessageProcessor
    {
        /// <summary>Maps a message-type identifier to its asynchronous handler (returns a <see cref="Task"/>).</summary>
        private readonly Dictionary<ushort, Func<byte[], Task>> _handlers = new Dictionary<ushort, Func<byte[], Task>>();

        /// <summary>Maps a message-type identifier to its synchronous, fire-and-return handler.</summary>
        private readonly Dictionary<ushort, Action<byte[]>> _handlersActions = new Dictionary<ushort, Action<byte[]>>();

        /// <summary>Invoked with (messageType, exception) when a handler throws, so failures aren't silently lost.</summary>
        public Action<ushort, Exception>? OnHandlerError;

        /// <summary>
        /// Registers an asynchronous handler for a message type. Re-registering the same type replaces the
        /// previous handler. Use this overload when handling involves awaitable work (I/O, async deserialization).
        /// </summary>
        /// <param name="type">The message-type identifier this handler should receive.</param>
        /// <param name="handler">The async callback invoked with the raw payload; its returned task is observed for faults.</param>
        public void RegisterHandler(ushort type, Func<byte[], Task> handler)
        {
            _handlers[type] = handler;
        }

        /// <summary>
        /// Registers a synchronous handler for a message type. Re-registering the same type replaces the
        /// previous handler. An async handler registered for the same type takes precedence during dispatch.
        /// </summary>
        /// <param name="type">The message-type identifier this handler should receive.</param>
        /// <param name="handler">The synchronous callback invoked with the raw payload.</param>
        public void RegisterHandler(ushort type, Action<byte[]> handler)
        {
            _handlersActions[type] = handler;
        }

        /// <summary>
        /// Dispatches a single message to its registered handler. The async handler for <paramref name="type"/>
        /// is preferred; if none exists, the synchronous handler is used; if neither is registered the message is
        /// dropped. Exceptions are never propagated to the caller — they are reported through
        /// <see cref="OnHandlerError"/> so a faulty handler cannot tear down the receive loop.
        /// </summary>
        /// <param name="type">The message-type identifier used to select the handler.</param>
        /// <param name="data">The raw, already-unframed payload passed to the handler.</param>
        /// <remarks>
        /// For async handlers: if the returned task is already completed and faulted, the error is reported
        /// immediately; if still running, it is observed in the background via <see cref="ObserveAsync"/>.
        /// Synchronous throws (or throws before an async handler's first await) are caught here.
        /// </remarks>
        public void ProcessMessage(ushort type, byte[] data)
        {
            try
            {
                if (_handlers.TryGetValue(type, out var handler))
                {
                    var task = handler(data);
                    if (task != null && !task.IsCompleted)
                        ObserveAsync(type, task);
                    else if (task != null && task.IsFaulted)
                        ReportError(type, task.Exception!);
                }
                else if (_handlersActions.TryGetValue(type, out var handlerAction))
                {
                    handlerAction(data);
                }
            }
            catch (Exception ex)
            {
                // Handler threw synchronously (before its first await, or a sync handler).
                ReportError(type, ex);
            }
        }

        /// <summary>
        /// Like <see cref="ProcessMessage"/> but returns a task that completes when the handler finishes,
        /// so a caller applying back-pressure can release a concurrency slot on completion. Exceptions are
        /// still reported via <see cref="OnHandlerError"/> and never propagate out of the returned task.
        /// </summary>
        /// <param name="type">The message-type identifier used to select the handler.</param>
        /// <param name="data">The raw, already-unframed payload passed to the handler.</param>
        /// <returns>A task that completes (always successfully) once the selected handler has finished.</returns>
        public Task ProcessMessageAsync(ushort type, byte[] data)
        {
            try
            {
                if (_handlers.TryGetValue(type, out var handler))
                    return ObserveCompletionAsync(type, handler(data) ?? Task.CompletedTask);

                if (_handlersActions.TryGetValue(type, out var handlerAction))
                    handlerAction(data);
            }
            catch (Exception ex)
            {
                ReportError(type, ex);
            }
            return Task.CompletedTask;
        }

        /// <summary>Awaits a handler task, reporting any fault, and returns a never-faulting task for the caller.</summary>
        /// <param name="type">The message-type identifier, for error reporting.</param>
        /// <param name="task">The handler task to observe.</param>
        /// <returns>A task that completes when <paramref name="task"/> finishes (faults are reported, not propagated).</returns>
        private async Task ObserveCompletionAsync(ushort type, Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { ReportError(type, ex); }
        }

        /// <summary>
        /// Reports a handler fault through <see cref="OnHandlerError"/>, swallowing any exception the sink itself
        /// throws. Critical for the <see cref="ObserveAsync"/> path: an exception escaping that <c>async void</c>
        /// observer (e.g. a misconfigured logger that throws) would otherwise crash the whole process.
        /// </summary>
        /// <param name="type">The message-type identifier, included in the error report.</param>
        /// <param name="ex">The handler exception to report.</param>
        private void ReportError(ushort type, Exception ex)
        {
            try { OnHandlerError?.Invoke(type, ex); }
            catch { /* a throwing error sink (e.g. faulty logger) must never escape and crash the process */ }
        }

        /// <summary>
        /// Awaits an in-flight handler task in the background and reports any fault through
        /// <see cref="OnHandlerError"/>. Declared <c>async void</c> deliberately: it is a fire-and-forget
        /// observer so a still-running async handler does not block <see cref="ProcessMessage"/>, while still
        /// ensuring its eventual exception is surfaced rather than lost as an unobserved task exception.
        /// </summary>
        /// <param name="type">The message-type identifier, included in the error report to identify the failing handler.</param>
        /// <param name="task">The handler task to observe to completion.</param>
        private async void ObserveAsync(ushort type, Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { ReportError(type, ex); }
        }
    }
}
