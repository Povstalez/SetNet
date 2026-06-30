using System.Threading.Tasks;

namespace SetNet.Data
{
    /// <summary>
    /// Contract for client-side message handlers that process messages pushed from the server.
    /// Implementations decorated with <see cref="SetNet.Data.Attributes.MessageHandlerAttribute"/> are
    /// discovered and registered automatically, then invoked by the client's message-routing layer when a
    /// packet of the matching type arrives. The library deserializes the payload (via the configured
    /// <see cref="SetNet.Messaging.SetNetSerializer"/>) and hands the handler the strongly-typed
    /// <typeparamref name="TMessage"/> directly. This is where application logic reacts to server updates.
    /// </summary>
    /// <typeparam name="TMessage">
    /// The message type this handler consumes. The wire payload is deserialized into this type before
    /// <see cref="HandleAsync"/> is called; both ends of the connection must agree on the serializer.
    /// </typeparam>
    public interface IClientMessageHandler<in TMessage>
    {
        /// <summary>
        /// Handles a single incoming message addressed to this client. Called by the routing layer once a
        /// packet whose type maps to this handler has been received, unframed, and deserialized into
        /// <typeparamref name="TMessage"/>; the implementation updates client state from it.
        /// </summary>
        /// <param name="message">The deserialized message instance.</param>
        /// <returns>A task that completes when the message has been fully processed.</returns>
        Task HandleAsync(TMessage message);
    }
}
