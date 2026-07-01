using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Data
{
    /// <summary>
    /// Contract for server-side message handlers that process messages received from a connected client.
    /// Implementations decorated with <see cref="SetNet.Data.Attributes.MessageHandlerAttribute"/> are
    /// discovered and registered automatically, then invoked by the server's routing layer when a packet of
    /// the matching type arrives from a peer. The library deserializes the payload (via the configured
    /// <see cref="SetNet.Messaging.SetNetSerializer"/>) and hands the handler the strongly-typed
    /// <typeparamref name="TMessage"/> directly. This is where server application logic processes client
    /// requests and sends responses back through the originating peer.
    /// </summary>
    /// <typeparam name="TMessage">
    /// The message type this handler consumes. The wire payload is deserialized into this type before
    /// <see cref="HandleAsync"/> is called; both ends of the connection must agree on the serializer.
    /// </typeparam>
    public interface IServerMessageHandler<in TMessage>
    {
        /// <summary>
        /// Handles a single incoming message from a specific client. Called by the routing layer once a packet
        /// whose type maps to this handler has been received, unframed, and deserialized into
        /// <typeparamref name="TMessage"/>; the implementation acts on it and replies via <paramref name="peer"/>.
        /// </summary>
        /// <param name="peer">
        /// The server-side peer representing the client that sent the message; used both to identify the sender
        /// and to send responses back over the same connection.
        /// </param>
        /// <param name="message">The deserialized message instance.</param>
        /// <returns>A task that completes when the message has been fully processed.</returns>
        Task HandleAsync(BasePeer peer, TMessage message);
    }
}
