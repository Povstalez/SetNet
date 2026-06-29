using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SetNet.Data.Attributes;

namespace SetNet.Core.Commands
{
    /// <summary>
    /// Reflection-based registry that auto-discovers and instantiates message handlers of a given handler
    /// interface <typeparamref name="T"/> (e.g. server-side or client-side handlers). It scans loaded
    /// assemblies for concrete classes that implement <typeparamref name="T"/> and carry a
    /// <see cref="MessageHandlerAttribute"/>, then maps each message type to a ready-to-use handler instance.
    /// This is the dispatch table the networking layer consults to route an incoming message to its handler.
    /// </summary>
    /// <typeparam name="T">
    /// The handler contract to discover (such as <c>IServerMessageHandler</c> or <c>IClientMessageHandler</c>).
    /// Only non-abstract, non-interface types assignable to <typeparamref name="T"/> are considered.
    /// </typeparam>
    public class CommandExecutor<T>
    {
        // The assembly scan is expensive; cache the discovered (messageType, handlerType) map per T.
        // Static fields on a generic type are per-constructed-type, so each handler interface caches
        // independently. Note: assemblies loaded after the first discovery are not re-scanned.

        /// <summary>
        /// Process-wide cache of the discovered (message type, handler type) pairs for this
        /// <typeparamref name="T"/>. Populated once on first construction and reused thereafter; assemblies
        /// loaded after that first scan are not picked up. <see langword="null"/> until discovery has run.
        /// </summary>
        private static (ushort MessageType, Type HandlerType)[]? _discovered;

        /// <summary>Guards the one-time population of <see cref="_discovered"/> against concurrent first constructions.</summary>
        private static readonly object _discoverLock = new object();

        /// <summary>Maps each registered message type to its single, eagerly created handler instance.</summary>
        protected readonly Dictionary<ushort, T> _handlers;

        /// <summary>The set of message types this executor has a handler for; a flat list mirroring the keys of <see cref="Handlers"/>.</summary>
        public List<ushort> Keys = new List<ushort>();

        /// <summary>The message-type-to-handler dispatch table used to route incoming messages.</summary>
        public Dictionary<ushort, T> Handlers => _handlers;

        /// <summary>
        /// Builds the dispatch table by running (or reusing the cached result of) assembly discovery and
        /// instantiating one handler per discovered message type. Exists so the networking layer can obtain a
        /// fully populated handler registry simply by constructing it, with no manual registration calls.
        /// </summary>
        /// <exception cref="MissingMethodException">
        /// Thrown by <see cref="Activator.CreateInstance(Type)"/> if a discovered handler type lacks a public
        /// parameterless constructor.
        /// </exception>
        public CommandExecutor()
        {
            _handlers = new Dictionary<ushort, T>();

            foreach (var (messageType, handlerType) in Discover())
                _handlers[messageType] = (T)Activator.CreateInstance(handlerType);

            Keys.AddRange(_handlers.Keys);
        }

        /// <summary>
        /// Lazily scans every assembly in the current <see cref="AppDomain"/> for concrete handler types of
        /// <typeparamref name="T"/> decorated with <see cref="MessageHandlerAttribute"/>, returning their
        /// (message type, type) pairs. Results are computed once and cached, since reflecting over all loaded
        /// assemblies is expensive and the handler set is effectively static for the process.
        /// </summary>
        /// <returns>
        /// The cached array of discovered (message type, handler <see cref="Type"/>) pairs for this
        /// <typeparamref name="T"/>; empty if no matching handlers exist.
        /// </returns>
        /// <remarks>
        /// Thread-safe via double-checked locking on <see cref="_discoverLock"/>. Assemblies whose types fail to
        /// load (<see cref="ReflectionTypeLoadException"/>) are tolerated by using only the types that did load.
        /// Assemblies loaded after the first successful discovery are not rescanned.
        /// </remarks>
        private static (ushort, Type)[] Discover()
        {
            if (_discovered != null)
                return _discovered;

            lock (_discoverLock)
            {
                if (_discovered != null)
                    return _discovered;

                var found = new List<(ushort, Type)>();
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
                        if (type == null || type.IsAbstract || type.IsInterface || !typeof(T).IsAssignableFrom(type))
                            continue;

                        var attr = type.GetCustomAttribute<MessageHandlerAttribute>();
                        if (attr == null) continue;

                        found.Add((attr.MessageType, type));
                    }
                }

                _discovered = found.ToArray();
                return _discovered;
            }
        }
    }
}
