using System;
using System.Collections.Generic;
using SetNet.Logging;

namespace SetNet.Events
{
    /// <summary>
    /// A lightweight string-keyed publish/subscribe hub used for decoupled, in-process communication between
    /// components (for example, notifying game or network listeners without direct references). Subscribers
    /// register callbacks against a named event and are invoked when that event is triggered. Exceptions thrown
    /// by individual handlers are caught and logged so one bad subscriber cannot break the dispatch of others.
    /// </summary>
    /// <remarks>
    /// This implementation is not thread-safe: subscribe, unsubscribe, and trigger all mutate or enumerate the
    /// same dictionary/lists without synchronization, so external coordination is required if used across threads.
    /// </remarks>
    public class EventManager
    {
        /// <summary>Maps each event name to its ordered list of subscribed handlers.</summary>
        private readonly Dictionary<string, List<Action<object>>> _events = new Dictionary<string, List<Action<object>>>();

        /// <summary>Sink used to report exceptions thrown by handlers; never null (a no-op logger is substituted).</summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Creates an event manager, optionally wiring in a logger for handler-failure diagnostics.
        /// </summary>
        /// <param name="logger">
        /// The logger used to record handler exceptions. When <c>null</c>, a <see cref="NoOpLogger"/> is used so
        /// the rest of the class can log unconditionally without null checks.
        /// </param>
        public EventManager(ILogger logger = null)
        {
            _logger = logger ?? new NoOpLogger();
        }

        /// <summary>
        /// Adds a handler to the subscriber list for the given event, creating the list on first subscription.
        /// The same handler may be added more than once, in which case it will be invoked once per registration.
        /// </summary>
        /// <param name="eventName">The name of the event to listen for.</param>
        /// <param name="handler">The callback invoked (with the event payload) each time the event is triggered.</param>
        public void Subscribe(string eventName, Action<object> handler)
        {
            if (!_events.ContainsKey(eventName))
                _events[eventName] = new List<Action<object>>();

            _events[eventName].Add(handler);
        }

        /// <summary>
        /// Removes a previously subscribed handler from an event so it stops receiving notifications. Only the
        /// first matching registration is removed; no error occurs if the event or handler is not present.
        /// </summary>
        /// <param name="eventName">The name of the event to stop listening to.</param>
        /// <param name="handler">The exact handler delegate that was passed to <see cref="Subscribe"/>.</param>
        public void Unsubscribe(string eventName, Action<object> handler)
        {
            if (_events.TryGetValue(eventName, out var handlers))
                handlers.Remove(handler);
        }

        /// <summary>
        /// Publishes an event, synchronously invoking every subscribed handler in registration order with the
        /// supplied payload. Each handler is invoked inside a try/catch so that a throwing subscriber is logged
        /// and skipped rather than aborting delivery to the remaining subscribers.
        /// </summary>
        /// <param name="eventName">The name of the event to raise; if no handlers are subscribed, the call is a no-op.</param>
        /// <param name="data">The payload passed to each handler; its concrete type is by convention between publisher and subscribers.</param>
        public void Trigger(string eventName, object data)
        {
            if (_events.TryGetValue(eventName, out var handlers))
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler?.Invoke(data);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"Error in handler for event '{eventName}': {ex.Message}", LogLevel.Error);
                    }
                }
            }
        }
    }
}