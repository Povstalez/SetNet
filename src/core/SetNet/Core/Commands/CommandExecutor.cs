using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Protocol;

namespace SetNet.Core.Commands
{
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
        public ServerCommandExecutor() : this(SetNetRuntime.Default) { }

        /// <summary>Discovers and instantiates handlers from a scoped runtime.</summary>
        public ServerCommandExecutor(SetNetRuntime runtime)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            foreach (var handlerRegistration in runtime.Handlers.DiscoverServerHandlers())
            {
                var handler = HandlerActivator.Create(handlerRegistration.HandlerType);
                var invoker = (IServerHandlerInvoker)Activator.CreateInstance(
                    typeof(ServerHandlerInvoker<>).MakeGenericType(handlerRegistration.MessageClrType),
                    handler,
                    runtime.Serializer);
                _handlers[handlerRegistration.MessageType] = invoker;
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
        private readonly SetNetRuntime _runtime;

        /// <summary>The message-type ids this executor has a handler for.</summary>
        public IReadOnlyList<ushort> Keys { get; }

        /// <summary>Discovers and instantiates one handler (wrapped in a typed invoker) per message type.</summary>
        /// <exception cref="MissingMethodException">If a discovered handler type lacks a public parameterless constructor.</exception>
        public ClientCommandExecutor() : this(SetNetRuntime.Default) { }

        /// <summary>Discovers and instantiates handlers from a scoped runtime.</summary>
        public ClientCommandExecutor(SetNetRuntime runtime)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            _runtime = runtime;
            foreach (var handlerRegistration in runtime.Handlers.DiscoverClientHandlers())
            {
                var handler = HandlerActivator.Create(handlerRegistration.HandlerType);
                var invoker = (IClientHandlerInvoker)Activator.CreateInstance(
                    typeof(ClientHandlerInvoker<>).MakeGenericType(handlerRegistration.MessageClrType),
                    handler,
                    runtime.Serializer);
                _handlers[handlerRegistration.MessageType] = invoker;
            }

            Keys = _handlers.Keys.ToArray();
        }

        /// <summary>Deserializes and routes <paramref name="data"/> to the handler registered for <paramref name="messageType"/>.</summary>
        internal Task DispatchAsync(ushort messageType, byte[] data)
        {
            if (messageType == ProtocolTypes.Envelope)
                return ProtocolDispatcher.DispatchClientAsync(_runtime, _runtime.Deserialize<byte[]>(data));
            return _handlers[messageType].InvokeAsync(data);
        }
    }
}
