using System;

namespace SetNet.Protocol
{
    /// <summary>
    /// Marks an <see cref="IChannelService"/> implementation as the server-side handler for one protocol channel.
    /// Discovered by reflection at first use (like SetNet's <c>[MessageHandler]</c> and RPC's <c>[RpcMethod]</c>),
    /// so referencing the module's assembly and enabling it is all that is needed — no manual registration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ProtocolChannelAttribute : Attribute
    {
        /// <summary>The channel id this service handles (see <see cref="Channels"/>).</summary>
        public ushort Channel { get; }

        /// <summary>Associates the decorated service with a channel id.</summary>
        /// <param name="channel">The channel id clients target with <c>RequestAsync</c>/<c>PostAsync</c>.</param>
        public ProtocolChannelAttribute(ushort channel) => Channel = channel;
    }
}
