using System.Threading.Tasks;

namespace SetNet.Data
{
    /// <summary>
    /// Contract for client-side message handlers that process payloads pushed from the server.
    /// Implementations decorated with <see cref="SetNet.Data.Attributes.MessageHandlerAttribute"/> are
    /// discovered and registered automatically, then invoked by the client's message-routing layer when a
    /// packet of the matching type arrives. This is where application logic reacts to server updates.
    /// </summary>
    public interface IClientMessageHandler
    {
        /// <summary>
        /// Handles a single incoming message addressed to this client. Called by the routing layer once a
        /// packet whose type maps to this handler has been received and unframed; the implementation typically
        /// deserializes <paramref name="data"/> into a strongly-typed message and updates client state.
        /// </summary>
        /// <param name="data">
        /// The raw, already-unframed message payload (without the length/type header). Usually fed to
        /// <see cref="SetNet.Messaging.SetNetSerializer.Deserialize{T}"/> to recover the typed message.
        /// </param>
        /// <returns>A task that completes when the message has been fully processed.</returns>
        Task HandleAsync(byte[] data);
    }
}