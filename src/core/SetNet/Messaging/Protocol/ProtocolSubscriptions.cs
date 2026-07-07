using System;

namespace SetNet.Protocol
{
    /// <summary>
    /// Process-wide registry of client-side push-event subscriptions, keyed by (channel, op). Every module that
    /// used to keep its own static "client registry + DispatchEvent" now routes server push events through here, so
    /// there is one shared subscription mechanism. Each callback receives the raw event body; typed
    /// <c>On&lt;T&gt;</c> overloads wrap a deserializing closure around it.
    /// </summary>
    /// <remarks>
    /// Subscriptions are process-wide (not scoped to a single <c>BaseClient</c>), matching the pre-existing
    /// one-client-per-process assumption of the module event registries. With multiple co-located clients an event
    /// is delivered to every subscriber for its (channel, op); modules that need to disambiguate include an
    /// identifier (e.g. a room code) in the body and filter inside the callback, exactly as before.
    /// </remarks>
    internal static class ProtocolSubscriptions
    {
        /// <summary>Adds a subscription for (channel, op); returns an <see cref="IDisposable"/> that removes it.</summary>
        public static IDisposable Add(ushort channel, ushort op, Action<byte[]> callback)
        {
            return SetNetRuntime.Default.ProtocolSubscriptions.Add(channel, op, callback);
        }

        /// <summary>Delivers an event body to every subscriber for (channel, op). Faulty callbacks are isolated.</summary>
        public static void Dispatch(ushort channel, ushort op, byte[] body)
        {
            SetNetRuntime.Default.ProtocolSubscriptions.Dispatch(channel, op, body);
        }

    }
}
