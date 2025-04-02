using System;
using System.Collections.Generic;

namespace SetNet.Events
{
    public class EventManager
    {
        private readonly Dictionary<string, Action<object>> _events = new Dictionary<string, Action<object>>();

        public void Subscribe(string eventName, Action<object> handler)
        {
            _events[eventName] = handler;
        }

        public void Trigger(string eventName, object data)
        {
            if (_events.TryGetValue(eventName, out var handler))
                handler(data);
        }
    }
}