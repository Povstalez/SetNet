using System.Threading.Tasks;
using SetNet.Data;
using SetNet.Messaging;

namespace SetNet.Core.Commands
{
    /// <summary>
    /// Non-generic server-side dispatch shim. The receive path works in raw <see cref="byte"/>[]; an invoker
    /// bridges that to a strongly-typed <see cref="IServerMessageHandler{TMessage}"/> by deserializing the
    /// payload first. One invoker is built per registered message type.
    /// </summary>
    internal interface IServerHandlerInvoker
    {
        /// <summary>Deserializes <paramref name="data"/> and forwards it to the typed handler.</summary>
        Task InvokeAsync(BasePeer peer, byte[] data);
    }

    /// <summary>Non-generic client-side counterpart of <see cref="IServerHandlerInvoker"/>.</summary>
    internal interface IClientHandlerInvoker
    {
        /// <summary>Deserializes <paramref name="data"/> and forwards it to the typed handler.</summary>
        Task InvokeAsync(byte[] data);
    }

    /// <summary>
    /// Adapts a typed <see cref="IServerMessageHandler{TMessage}"/> to the non-generic
    /// <see cref="IServerHandlerInvoker"/>, deserializing the wire payload into <typeparamref name="TMessage"/>
    /// via the process-wide <see cref="SetNetSerializer"/> before invoking the handler.
    /// </summary>
    /// <typeparam name="TMessage">The message type the wrapped handler consumes.</typeparam>
    internal sealed class ServerHandlerInvoker<TMessage> : IServerHandlerInvoker
    {
        private readonly IServerMessageHandler<TMessage> _handler;

        /// <summary>Wraps the given typed handler.</summary>
        public ServerHandlerInvoker(IServerMessageHandler<TMessage> handler) => _handler = handler;

        /// <inheritdoc/>
        public Task InvokeAsync(BasePeer peer, byte[] data)
            => _handler.HandleAsync(peer, SetNetSerializer.Deserialize<TMessage>(data));
    }

    /// <summary>
    /// Adapts a typed <see cref="IClientMessageHandler{TMessage}"/> to the non-generic
    /// <see cref="IClientHandlerInvoker"/>, deserializing the wire payload into <typeparamref name="TMessage"/>
    /// via the process-wide <see cref="SetNetSerializer"/> before invoking the handler.
    /// </summary>
    /// <typeparam name="TMessage">The message type the wrapped handler consumes.</typeparam>
    internal sealed class ClientHandlerInvoker<TMessage> : IClientHandlerInvoker
    {
        private readonly IClientMessageHandler<TMessage> _handler;

        /// <summary>Wraps the given typed handler.</summary>
        public ClientHandlerInvoker(IClientMessageHandler<TMessage> handler) => _handler = handler;

        /// <inheritdoc/>
        public Task InvokeAsync(byte[] data)
            => _handler.HandleAsync(SetNetSerializer.Deserialize<TMessage>(data));
    }
}
