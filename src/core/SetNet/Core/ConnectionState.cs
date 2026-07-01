namespace SetNet.Core
{
    /// <summary>
    /// The lifecycle phase of a client connection. Drives the connection state machine in the core client,
    /// guarding against overlapping operations (e.g. connecting while already connected) and signalling which
    /// lifecycle callbacks are appropriate to raise.
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>No active connection: the initial state and the state after a clean or failed teardown.</summary>
        Disconnected,

        /// <summary>An initial connect attempt is in progress and has not yet succeeded or failed.</summary>
        Connecting,

        /// <summary>A connection is established and the receive loop is actively running.</summary>
        Connected,

        /// <summary>The connection was lost unexpectedly and an automatic reconnect attempt is in progress.</summary>
        Reconnecting,

        /// <summary>A graceful, caller-initiated disconnect is in progress (tearing the connection down).</summary>
        Disconnecting
    }
}
