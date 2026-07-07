using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Core.Commands
{
    /// <summary>Controls how duplicate handler registrations for the same wire type are handled.</summary>
    public enum DuplicateHandlerBehavior
    {
        /// <summary>Keep the last registration, matching SetNet's historical behaviour.</summary>
        Replace,

        /// <summary>Throw during executor construction when two handlers claim the same wire type on the same side.</summary>
        Throw
    }

    /// <summary>Explicit and assembly-based handler catalog for a <see cref="SetNetRuntime"/>.</summary>
    public sealed class HandlerRegistry
    {
        private readonly object _gate = new object();
        private readonly List<Assembly> _assemblies = new List<Assembly>();
        private readonly List<HandlerRegistration> _serverHandlers = new List<HandlerRegistration>();
        private readonly List<HandlerRegistration> _clientHandlers = new List<HandlerRegistration>();

        /// <summary>
        /// When true, executor construction also scans all currently loaded assemblies. Keep enabled for backwards
        /// compatibility; disable it for plugin hosts that want fully explicit registration.
        /// </summary>
        public bool AutoDiscoverLoadedAssemblies { get; set; } = true;

        /// <summary>Duplicate handler policy. Defaults to Replace for backwards compatibility.</summary>
        public DuplicateHandlerBehavior DuplicateBehavior { get; set; } = DuplicateHandlerBehavior.Replace;

        /// <summary>Registers an assembly to scan every time a client/server executor is built.</summary>
        public HandlerRegistry AddHandlersFromAssembly(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            lock (_gate)
            {
                if (!_assemblies.Contains(assembly))
                    _assemblies.Add(assembly);
            }
            return this;
        }

        /// <summary>Registers the assembly containing <typeparamref name="T"/> for handler discovery.</summary>
        public HandlerRegistry AddHandlersFromAssemblyOf<T>() => AddHandlersFromAssembly(typeof(T).Assembly);

        /// <summary>Registers a concrete server handler type for a wire type.</summary>
        public HandlerRegistry AddServerHandler<TMessage, THandler>(ushort messageType)
            where THandler : IServerMessageHandler<TMessage>
        {
            lock (_gate) _serverHandlers.Add(new HandlerRegistration(messageType, typeof(THandler), typeof(TMessage)));
            return this;
        }

        /// <summary>Registers a concrete client handler type for a wire type.</summary>
        public HandlerRegistry AddClientHandler<TMessage, THandler>(ushort messageType)
            where THandler : IClientMessageHandler<TMessage>
        {
            lock (_gate) _clientHandlers.Add(new HandlerRegistration(messageType, typeof(THandler), typeof(TMessage)));
            return this;
        }

        internal HandlerRegistration[] DiscoverServerHandlers()
            => Discover(typeof(IServerMessageHandler<>), _serverHandlers);

        internal HandlerRegistration[] DiscoverClientHandlers()
            => Discover(typeof(IClientMessageHandler<>), _clientHandlers);

        private HandlerRegistration[] Discover(Type openHandlerInterface, List<HandlerRegistration> explicitHandlers)
        {
            List<Assembly> assemblies;
            HandlerRegistration[] explicitSnapshot;
            lock (_gate)
            {
                assemblies = new List<Assembly>(_assemblies);
                explicitSnapshot = explicitHandlers.ToArray();
            }

            if (AutoDiscoverLoadedAssemblies)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assemblies.Contains(assembly))
                        assemblies.Add(assembly);
            }

            var found = new List<HandlerRegistration>(explicitSnapshot);
            foreach (var assembly in assemblies)
                ScanAssembly(assembly, openHandlerInterface, found);

            return ApplyDuplicatePolicy(found);
        }

        private HandlerRegistration[] ApplyDuplicatePolicy(List<HandlerRegistration> handlers)
        {
            var map = new Dictionary<ushort, HandlerRegistration>();
            foreach (var handler in handlers)
            {
                if (map.TryGetValue(handler.MessageType, out var existing) && DuplicateBehavior == DuplicateHandlerBehavior.Throw)
                {
                    throw new InvalidOperationException(
                        $"Duplicate SetNet handler for wire type {handler.MessageType}: " +
                        $"{existing.HandlerType.FullName} and {handler.HandlerType.FullName}.");
                }

                map[handler.MessageType] = handler;
            }

            return map.Values.ToArray();
        }

        private static void ScanAssembly(Assembly assembly, Type openHandlerInterface, List<HandlerRegistration> found)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface)
                    continue;

                var closed = type.GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == openHandlerInterface);
                if (closed == null) continue;

                var attr = type.GetCustomAttribute<MessageHandlerAttribute>();
                if (attr == null) continue;

                found.Add(new HandlerRegistration(attr.MessageType, type, closed.GetGenericArguments()[0]));
            }
        }
    }

    internal readonly struct HandlerRegistration
    {
        public HandlerRegistration(ushort messageType, Type handlerType, Type messageClrType)
        {
            MessageType = messageType;
            HandlerType = handlerType;
            MessageClrType = messageClrType;
        }

        public ushort MessageType { get; }
        public Type HandlerType { get; }
        public Type MessageClrType { get; }
    }
}
