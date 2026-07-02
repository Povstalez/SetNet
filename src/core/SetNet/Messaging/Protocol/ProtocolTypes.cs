namespace SetNet.Protocol
{
    /// <summary>
    /// The single reserved wire message-type id that carries <b>all</b> unified-protocol traffic — requests,
    /// replies, fire-and-forget messages and server push events for every channel (Rooms, Party, Matchmaking,
    /// Inventory, …). One id suffices because the client and server maintain independent dispatch tables: the
    /// server registers an <see cref="System.Byte"/>[] handler for this id and the client registers its own, so a
    /// frame is routed to the correct side by direction, and everything within is demultiplexed by
    /// <see cref="ProtocolEnvelope.Channel"/> / <see cref="ProtocolEnvelope.Op"/>.
    /// </summary>
    /// <remarks>
    /// This collapses the former per-module reserved-id sprawl (each module used to reserve a Command/Reply/Event
    /// triple in the 65448–65535 range) down to this one envelope id. It sits just below that historical range so
    /// it does not collide with any still-unmigrated module.
    /// </remarks>
    public static class ProtocolTypes
    {
        /// <summary>Wire type id for the unified protocol envelope (both directions).</summary>
        public const ushort Envelope = ushort.MaxValue - 88;   // 65447
    }

    /// <summary>
    /// The role of a <see cref="ProtocolEnvelope"/>, telling the receiving side how to treat it: a one-way message,
    /// a correlated request expecting a reply, the reply/error to such a request, or an unsolicited push event.
    /// </summary>
    internal enum ProtocolKind : byte
    {
        /// <summary>Client → server, fire-and-forget: no reply is expected (correlation id is 0).</summary>
        Send = 0,

        /// <summary>Client → server, correlated: the server should answer with a <see cref="Reply"/> or <see cref="Error"/> carrying the same correlation id.</summary>
        Request = 1,

        /// <summary>Server → client: the successful answer to a <see cref="Request"/>, matched by correlation id.</summary>
        Reply = 2,

        /// <summary>Server → client: an unsolicited push event for a channel/op (correlation id is 0), routed to subscribers.</summary>
        Event = 3,

        /// <summary>Server → client: the failed answer to a <see cref="Request"/> — the body is a UTF-8 error message the caller re-throws as a <see cref="ProtocolException"/>.</summary>
        Error = 4,
    }
}
