using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
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
            {
                // Skip the wrap header in place instead of deserializing a copy.
                //
                // Deserialize<byte[]> here produced a second array holding byte for byte what `data` already held,
                // minus the two or three header bytes — one throwaway array per delivered event, which in a game
                // client taking hundreds of events per tick is a steady stream of garbage for no gain. The
                // dispatcher below reads the envelope through a window anyway (see its ReadOnlyMemory overload).
                int skip = WrapHeaderSize(data);
                if (skip >= 0)
                    return ProtocolDispatcher.DispatchClientAsync(_runtime, data.AsMemory(skip));

                // Serializer that does not frame binary payloads (JSON and friends): keep the old path, it is the
                // only one that can undo whatever wrapping it used.
                return ProtocolDispatcher.DispatchClientAsync(_runtime, _runtime.Deserialize<byte[]>(data));
            }
            return _handlers[messageType].InvokeAsync(data);
        }

        /// <summary>
        /// Length of the serializer's wrap header in front of a raw binary payload, or -1 when this serializer does
        /// not frame binary payloads.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="IBinaryFrameSerializer.MeasureBinaryFrameHeader"/> rather than from knowledge of
        /// any particular format: for a given total length exactly one header size h satisfies
        /// <c>MeasureBinaryFrameHeader(total - h) == h</c>, because the header grows with the payload monotonically
        /// and in steps. Keeping the format knowledge inside the serializer is the whole point of the interface.
        /// </remarks>
        private int WrapHeaderSize(byte[] data)
        {
            if (data == null || !(_runtime.Serializer is IBinaryFrameSerializer framer)) return -1;

            for (int h = 1; h <= 8 && h < data.Length; h++)
                if (framer.MeasureBinaryFrameHeader(data.Length - h) == h) return h;

            return -1;
        }
    }
}
