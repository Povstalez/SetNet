using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Protocol
{
    /// <summary>
    /// Server-side entry point for the unified protocol: an auto-discovered <c>[MessageHandler]</c> for the single
    /// reserved <see cref="ProtocolTypes.Envelope"/> type. It lives in the core assembly, so it is always
    /// discovered — no per-module <c>Runtime.Enable()</c> is needed to route protocol traffic. The message type is
    /// <c>byte[]</c> so the envelope rides over any configured serializer.
    /// </summary>
    [MessageHandler(ProtocolTypes.Envelope)]
    public sealed class ProtocolServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data) => ProtocolDispatcher.DispatchServerAsync(peer, data);
    }

    /// <summary>
    /// Client-side entry point for the unified protocol: an auto-discovered <c>[MessageHandler]</c> for the single
    /// reserved <see cref="ProtocolTypes.Envelope"/> type. It completes awaiting requests and routes push events to
    /// subscribers. Connection-less by design — correlation ids are process-unique.
    /// </summary>
    [MessageHandler(ProtocolTypes.Envelope)]
    public sealed class ProtocolClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) => ProtocolDispatcher.DispatchClientAsync(data);
    }
}
