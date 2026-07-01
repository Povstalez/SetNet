using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Core.Commands
{
    /// <summary>
    /// Reflection-based discovery of typed message handlers. Scans every loaded assembly once (results cached
    /// per handler interface) for concrete classes that implement the given open-generic handler interface
    /// (e.g. <see cref="IServerMessageHandler{TMessage}"/>) and carry a <see cref="MessageHandlerAttribute"/>.
    /// </summary>
    internal static class HandlerDiscovery
    {
        // Reflecting over all loaded assemblies is expensive and the handler set is effectively static for the
        // process, so cache per open-generic handler interface. The factory is idempotent, so the rare double
        // run under contention is harmless. Assemblies loaded after the first scan are not rescanned.
        private static readonly ConcurrentDictionary<Type, (ushort MessageType, Type HandlerType, Type MessageClrType)[]> _cache
            = new ConcurrentDictionary<Type, (ushort, Type, Type)[]>();

        /// <summary>
        /// Returns the discovered (message type id, handler class, message CLR type) triples for the given
        /// open-generic handler interface, e.g. <c>typeof(IServerMessageHandler&lt;&gt;)</c>.
        /// </summary>
        public static (ushort MessageType, Type HandlerType, Type MessageClrType)[] Discover(Type openHandlerInterface)
            => _cache.GetOrAdd(openHandlerInterface, Scan);

        private static (ushort, Type, Type)[] Scan(Type openHandlerInterface)
        {
            var found = new List<(ushort, Type, Type)>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || type.IsInterface)
                        continue;

                    // Find the closed handler interface this type implements to learn its message CLR type.
                    var closed = type.GetInterfaces().FirstOrDefault(i =>
                        i.IsGenericType && i.GetGenericTypeDefinition() == openHandlerInterface);
                    if (closed == null) continue;

                    var attr = type.GetCustomAttribute<MessageHandlerAttribute>();
                    if (attr == null) continue;

                    found.Add((attr.MessageType, type, closed.GetGenericArguments()[0]));
                }
            }

            return found.ToArray();
        }
    }

    /// <summary>
    /// Server-side dispatch table: maps each registered message-type id to an invoker that deserializes the
    /// payload and calls the matching typed <see cref="IServerMessageHandler{TMessage}"/>. Built simply by
    /// constructing it — handlers are discovered and instantiated automatically, with no manual registration.
    /// </summary>
    public sealed class ServerCommandExecutor
    {
        private readonly Dictionary<ushort, IServerHandlerInvoker> _handlers = new Dictionary<ushort, IServerHandlerInvoker>();

        /// <summary>The message-type ids this executor has a handler for.</summary>
        public IReadOnlyList<ushort> Keys { get; }

        /// <summary>Discovers and instantiates one handler (wrapped in a typed invoker) per message type.</summary>
        /// <exception cref="MissingMethodException">If a discovered handler type lacks a public parameterless constructor.</exception>
        public ServerCommandExecutor()
        {
            foreach (var (messageType, handlerType, messageClrType) in HandlerDiscovery.Discover(typeof(IServerMessageHandler<>)))
            {
                var handler = Activator.CreateInstance(handlerType);
                var invoker = (IServerHandlerInvoker)Activator.CreateInstance(
                    typeof(ServerHandlerInvoker<>).MakeGenericType(messageClrType), handler);
                _handlers[messageType] = invoker;
            }

            Keys = _handlers.Keys.ToArray();
        }

        /// <summary>Deserializes and routes <paramref name="data"/> to the handler registered for <paramref name="messageType"/>.</summary>
        internal Task DispatchAsync(ushort messageType, BasePeer peer, byte[] data)
            => _handlers[messageType].InvokeAsync(peer, data);
    }

    /// <summary>
    /// Client-side dispatch table: maps each registered message-type id to an invoker that deserializes the
    /// payload and calls the matching typed <see cref="IClientMessageHandler{TMessage}"/>. Built simply by
    /// constructing it — handlers are discovered and instantiated automatically, with no manual registration.
    /// </summary>
    public sealed class ClientCommandExecutor
    {
        private readonly Dictionary<ushort, IClientHandlerInvoker> _handlers = new Dictionary<ushort, IClientHandlerInvoker>();

        /// <summary>The message-type ids this executor has a handler for.</summary>
        public IReadOnlyList<ushort> Keys { get; }

        /// <summary>Discovers and instantiates one handler (wrapped in a typed invoker) per message type.</summary>
        /// <exception cref="MissingMethodException">If a discovered handler type lacks a public parameterless constructor.</exception>
        public ClientCommandExecutor()
        {
            foreach (var (messageType, handlerType, messageClrType) in HandlerDiscovery.Discover(typeof(IClientMessageHandler<>)))
            {
                var handler = Activator.CreateInstance(handlerType);
                var invoker = (IClientHandlerInvoker)Activator.CreateInstance(
                    typeof(ClientHandlerInvoker<>).MakeGenericType(messageClrType), handler);
                _handlers[messageType] = invoker;
            }

            Keys = _handlers.Keys.ToArray();
        }

        /// <summary>Deserializes and routes <paramref name="data"/> to the handler registered for <paramref name="messageType"/>.</summary>
        internal Task DispatchAsync(ushort messageType, byte[] data)
            => _handlers[messageType].InvokeAsync(data);
    }
}
