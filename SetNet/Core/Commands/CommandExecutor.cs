using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SetNet.Data.Attributes;

namespace SetNet.Core.Commands
{
    public class CommandExecutor<T>
    {
        protected readonly Dictionary<ushort, T> _handlers;

        public List<ushort> Keys = new List<ushort>();
        public Dictionary<ushort, T> Handlers => _handlers;

        public CommandExecutor()
        {
        
            _handlers = new Dictionary<ushort, T>();

            RegisterDataHandlers();
        }

        private void RegisterDataHandlers()
        {
            var handlerTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        
            foreach (var type in handlerTypes)
            {
                var attr = type.GetCustomAttribute<MessageHandlerAttribute>();
                if (attr == null) continue;
        
                var handler = (T)Activator.CreateInstance(type);
                _handlers[(ushort)attr.MessageType] = handler;
            }
        
            Keys.AddRange(_handlers.Keys);
        }
    }
}