namespace SetNet.Core
{
    /// <summary>
    /// Reserved message-type identifiers used by the transport itself rather than by application handlers.
    /// They live at the very top of the <see cref="ushort"/> range so they never collide with user-defined
    /// message types (which start low) and are filtered out before messages reach application dispatch.
    /// </summary>
    internal static class SystemMessageTypes
    {
        /// <summary>Heartbeat probe sent to verify the peer is still alive; the receiver replies with <see cref="Pong"/>.</summary>
        internal const ushort Ping = ushort.MaxValue - 1;          // 65534

        /// <summary>Heartbeat reply to a received <see cref="Ping"/>, confirming liveness and refreshing the timeout window.</summary>
        internal const ushort Pong = ushort.MaxValue;              // 65535

        /// <summary>Both mode: TCP-side message carrying the UDP bind token (server → client).</summary>
        internal const ushort UdpBindToken = ushort.MaxValue - 2;  // 65533

        /// <summary>
        /// True if <paramref name="type"/> is a reserved transport/system message rather than an application
        /// message. Used to keep system frames (heartbeat, bind token) out of application-facing hooks such as
        /// the raw-frame interceptor.
        /// </summary>
        /// <param name="type">The wire type id to test.</param>
        /// <returns><see langword="true"/> for a reserved system type; otherwise <see langword="false"/>.</returns>
        internal static bool IsSystem(ushort type) => type == Ping || type == Pong || type == UdpBindToken;
    }
}
