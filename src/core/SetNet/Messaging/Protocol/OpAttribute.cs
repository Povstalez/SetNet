using System;

namespace SetNet.Protocol
{
    /// <summary>
    /// Marks a method on a <see cref="ProtocolChannelAttribute"/> class as the handler for one channel operation, so
    /// a channel can be written as many small methods (one per op) instead of one big <see cref="IChannelService"/>
    /// <c>switch</c>. Discovered at first use (like <c>[MessageHandler]</c>/<c>[RpcMethod]</c>) and wired by
    /// <see cref="OpRouter"/>.
    /// </summary>
    /// <remarks>
    /// The method's parameters are bound by type — any of <c>BasePeer</c>, <c>ChannelRequest</c>, <c>byte[]</c> (the
    /// raw body), and at most one other type (the body, deserialized via <c>SetNetSerializer</c>) — in any order. The
    /// return type decides the reply: a value / <c>Task&lt;T&gt;</c> is serialized and sent as the reply, a
    /// <c>byte[]</c> / <c>Task&lt;byte[]&gt;</c> is sent raw, and <c>void</c> / <c>Task</c> sends nothing (use it for
    /// fire-and-forget ops, or reply yourself via a <c>ChannelRequest</c> parameter). Throw to fail a request.
    /// A class that implements <see cref="IChannelService"/> keeps full manual control and its <c>[Op]</c> methods
    /// (if any) are ignored.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OpAttribute : Attribute
    {
        /// <summary>The operation id within the channel this method handles.</summary>
        public ushort Op { get; }

        /// <summary>Associates the decorated method with a channel operation id.</summary>
        /// <param name="op">The op id (usually a cast from the module's op enum, e.g. <c>(ushort)RoomOp.Create</c>).</param>
        public OpAttribute(ushort op) => Op = op;
    }
}
