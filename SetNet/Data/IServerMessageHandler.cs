using System.Threading.Tasks;
using SetNet.Core;

namespace SetNet.Data
{
    /// <summary>
    /// Contract for server-side message handlers that process payloads received from a connected client.
    /// Implementations decorated with <see cref="SetNet.Data.Attributes.MessageHandlerAttribute"/> are
    /// discovered and registered automatically, then invoked by the server's routing layer when a packet of
    /// the matching type arrives from a peer. This is where server application logic processes client requests
    /// and sends responses back through the originating peer.
    /// </summary>
    public interface IServerMessageHandler
    {
        /// <summary>
        /// Handles a single incoming message from a specific client. Called by the routing layer once a packet
        /// whose type maps to this handler has been received and unframed; the implementation typically
        /// deserializes <paramref name="data"/>, acts on it, and replies via <paramref name="peer"/>.
        /// </summary>
        /// <param name="peer">
        /// The server-side peer representing the client that sent the message; used both to identify the sender
        /// and to send responses back over the same connection.
        /// </param>
        /// <param name="data">
        /// The raw, already-unframed message payload (without the length/type header). Usually fed to
        /// <see cref="SetNet.Messaging.MessagePackSerializer.Deserialize{T}"/> to recover the typed message.
        /// </param>
        /// <returns>A task that completes when the message has been fully processed.</returns>
        Task HandleAsync(BasePeer peer, byte[] data);
    }
}