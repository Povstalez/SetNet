using System.Threading;

namespace SetNet.Diagnostics
{
    /// <summary>
    /// Thread-safe, lightweight counters the library increments at key points so an operator can observe
    /// traffic and health (throughput, connection churn, reliability behaviour) and export them to their own
    /// monitoring. All counters are monotonic since the last <see cref="Reset"/>. Increments are internal;
    /// consumers read the public properties or take a <see cref="Snapshot"/>.
    /// </summary>
    public sealed class NetworkMetrics
    {
        private long _messagesSent;
        private long _messagesReceived;
        private long _connectionsAccepted;
        private long _connectionsRejected;
        private long _reliableRetransmits;
        private long _reliableAcksReceived;
        private long _handshakesDropped;
        private long _inboundDropped;

        /// <summary>Total application messages handed to the transport for sending.</summary>
        public long MessagesSent => Interlocked.Read(ref _messagesSent);

        /// <summary>Total application messages received and dispatched to handlers.</summary>
        public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

        /// <summary>Total client connections accepted by the server.</summary>
        public long ConnectionsAccepted => Interlocked.Read(ref _connectionsAccepted);

        /// <summary>Total inbound connections rejected (capacity or rate limit).</summary>
        public long ConnectionsRejected => Interlocked.Read(ref _connectionsRejected);

        /// <summary>Total reliable-UDP datagrams retransmitted due to missing ACKs.</summary>
        public long ReliableRetransmits => Interlocked.Read(ref _reliableRetransmits);

        /// <summary>Total reliable-UDP ACK datagrams received.</summary>
        public long ReliableAcksReceived => Interlocked.Read(ref _reliableAcksReceived);

        /// <summary>Total UDP handshakes dropped because the peer cap was reached (flood protection).</summary>
        public long HandshakesDropped => Interlocked.Read(ref _handshakesDropped);

        /// <summary>Total inbound messages dropped because a connection's inbound queue was at capacity (back-pressure overflow).</summary>
        public long InboundDropped => Interlocked.Read(ref _inboundDropped);

        internal void IncrementMessagesSent() => Interlocked.Increment(ref _messagesSent);
        internal void IncrementMessagesReceived() => Interlocked.Increment(ref _messagesReceived);
        internal void IncrementConnectionsAccepted() => Interlocked.Increment(ref _connectionsAccepted);
        internal void IncrementConnectionsRejected() => Interlocked.Increment(ref _connectionsRejected);
        internal void IncrementReliableRetransmits() => Interlocked.Increment(ref _reliableRetransmits);
        internal void IncrementReliableAcksReceived() => Interlocked.Increment(ref _reliableAcksReceived);
        internal void IncrementHandshakesDropped() => Interlocked.Increment(ref _handshakesDropped);
        internal void IncrementInboundDropped() => Interlocked.Increment(ref _inboundDropped);

        /// <summary>Takes a consistent-enough point-in-time snapshot of all counters for export/logging.</summary>
        /// <returns>An immutable snapshot of the current counter values.</returns>
        public NetworkMetricsSnapshot Snapshot() => new NetworkMetricsSnapshot(
            MessagesSent, MessagesReceived, ConnectionsAccepted, ConnectionsRejected,
            ReliableRetransmits, ReliableAcksReceived, HandshakesDropped, InboundDropped);

        /// <summary>Resets all counters to zero (e.g. for periodic interval reporting).</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _messagesSent, 0);
            Interlocked.Exchange(ref _messagesReceived, 0);
            Interlocked.Exchange(ref _connectionsAccepted, 0);
            Interlocked.Exchange(ref _connectionsRejected, 0);
            Interlocked.Exchange(ref _reliableRetransmits, 0);
            Interlocked.Exchange(ref _reliableAcksReceived, 0);
            Interlocked.Exchange(ref _handshakesDropped, 0);
            Interlocked.Exchange(ref _inboundDropped, 0);
        }
    }

    /// <summary>Immutable point-in-time view of <see cref="NetworkMetrics"/> counters.</summary>
    public readonly struct NetworkMetricsSnapshot
    {
        /// <summary>Application messages sent.</summary>
        public long MessagesSent { get; }
        /// <summary>Application messages received.</summary>
        public long MessagesReceived { get; }
        /// <summary>Connections accepted.</summary>
        public long ConnectionsAccepted { get; }
        /// <summary>Connections rejected (capacity/rate limit).</summary>
        public long ConnectionsRejected { get; }
        /// <summary>Reliable-UDP retransmits.</summary>
        public long ReliableRetransmits { get; }
        /// <summary>Reliable-UDP ACKs received.</summary>
        public long ReliableAcksReceived { get; }
        /// <summary>UDP handshakes dropped by the peer cap.</summary>
        public long HandshakesDropped { get; }

        /// <summary>Inbound messages dropped by the inbound-queue cap.</summary>
        public long InboundDropped { get; }

        /// <summary>Creates a snapshot with the given counter values.</summary>
        public NetworkMetricsSnapshot(long messagesSent, long messagesReceived, long connectionsAccepted,
            long connectionsRejected, long reliableRetransmits, long reliableAcksReceived, long handshakesDropped,
            long inboundDropped)
        {
            MessagesSent = messagesSent;
            MessagesReceived = messagesReceived;
            ConnectionsAccepted = connectionsAccepted;
            ConnectionsRejected = connectionsRejected;
            ReliableRetransmits = reliableRetransmits;
            ReliableAcksReceived = reliableAcksReceived;
            HandshakesDropped = handshakesDropped;
            InboundDropped = inboundDropped;
        }

        /// <summary>Renders the snapshot as a compact single-line string for logging.</summary>
        /// <returns>A human-readable summary of all counters.</returns>
        public override string ToString() =>
            $"sent={MessagesSent} recv={MessagesReceived} accepted={ConnectionsAccepted} rejected={ConnectionsRejected} " +
            $"retransmits={ReliableRetransmits} acks={ReliableAcksReceived} handshakesDropped={HandshakesDropped} inboundDropped={InboundDropped}";
    }
}
