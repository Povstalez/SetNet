using System;
using System.Collections.Generic;
using SetNet.Logging;

namespace SetNet.Events
{
    public class EventManager
    {
        private readonly Dictionary<string, List<Action<object>>> _events = new Dictionary<string, List<Action<object>>>();
        private readonly ILogger _logger;

        public EventManager(ILogger logger = null)
        {
            _logger = logger ?? new NoOpLogger();
        }

        public void Subscribe(string eventName, Action<object> handler)
        {
            if (!_events.ContainsKey(eventName))
                _events[eventName] = new List<Action<object>>();

            _events[eventName].Add(handler);
        }

        public void Unsubscribe(string eventName, Action<object> handler)
        {
            if (_events.TryGetValue(eventName, out var handlers))
                handlers.Remove(handler);
        }

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