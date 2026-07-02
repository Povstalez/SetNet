using System;

namespace SetNet.Protocol
{
    /// <summary>
    /// Marks a method on a <see cref="ProtocolChannelAttribute"/> class as a client-side handler for one channel
    /// push event — the attribute analog of the imperative <c>client.On&lt;T&gt;(channel, op, …)</c>. Discovered and
    /// auto-subscribed the first time a protocol event is dispatched on the client, so you can declare event handlers
    /// declaratively (like a client `[MessageHandler]`) instead of wiring every subscription by hand.
    /// </summary>
    /// <remarks>
    /// The method takes the event body — either a typed <c>T</c> (deserialized via <c>SetNetSerializer</c>), a
    /// <c>byte[]</c> (raw body), or no parameter — and returns <c>void</c> or <c>Task</c> (async handlers run
    /// fire-and-forget; exceptions are isolated). Handler instances are process-wide singletons (like client
    /// <c>[MessageHandler]</c>s and the module event registries), so use them for stateless/app-singleton reactions;
    /// for handlers that must close over per-instance state (a driver holding room state, say), keep the imperative
    /// <c>On&lt;T&gt;</c>. Both fire for the same (channel, op) — you can mix the two freely.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class EventAttribute : Attribute
    {
        /// <summary>The event op id within the channel this method handles.</summary>
        public ushort Op { get; }

        /// <summary>Associates the decorated method with a channel event op id.</summary>
        /// <param name="op">The event op id (usually a cast from the module's event enum, e.g. <c>(ushort)RoomEvt.PlayerJoined</c>).</param>
        public EventAttribute(ushort op) => Op = op;
    }
}
