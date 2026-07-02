using System.Threading.Tasks;

namespace SetNet.Protocol
{
    /// <summary>
    /// Server-side handler for one protocol channel. A single implementation (decorated with
    /// <see cref="ProtocolChannelAttribute"/>) receives every request and fire-and-forget message for its channel
    /// and dispatches on <see cref="ChannelRequest.Op"/> — replacing the per-module <c>[MessageHandler]</c> classes,
    /// hand-rolled correlation registries and reply framing with one uniform entry point.
    /// </summary>
    public interface IChannelService
    {
        /// <summary>
        /// Handles one inbound message for this channel. Read the body via <see cref="ChannelRequest.RawBody"/> /
        /// <see cref="ChannelRequest.Read{T}"/>, and for a request answer via <see cref="ChannelRequest.ReplyRawAsync"/> /
        /// <see cref="ChannelRequest.ReplyAsync{T}"/>. Throwing sends an error reply back to a waiting caller.
        /// </summary>
        /// <param name="request">The decoded request context (peer, op, body, correlation).</param>
        Task HandleAsync(ChannelRequest request);
    }
}
