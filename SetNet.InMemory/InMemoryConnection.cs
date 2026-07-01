using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport;

namespace SetNet.InMemory
{
    /// <summary>
    /// One end of an in-process, loopback connection. Two <see cref="InMemoryConnection"/> objects are created as a
    /// linked pair (client end + server end); each holds its own inbound queue and a reference to its peer, so a
    /// <see cref="SendAsync"/> on one end enqueues a framed message into the other end's inbound queue. There is no
    /// socket, no serialization boundary crossing a real wire, and no length framing — messages move as whole
    /// <see cref="TransportMessage"/> values, reliably and in order (like TCP), so <see cref="DeliveryMethod"/> and
    /// channel are ignored. The payload is copied on send so the two ends never share a mutable buffer.
    /// </summary>
    internal sealed class InMemoryConnection : ITransportConnection
    {
        private readonly AsyncChannel<TransportMessage> _inbound = new AsyncChannel<TransportMessage>();
        private InMemoryConnection _peer = null!;   // set immediately after construction in CreatePair
        private int _closed;

        private InMemoryConnection() { }

        /// <summary>Creates a linked client/server pair sharing an in-memory loopback.</summary>
        public static (InMemoryConnection client, InMemoryConnection server) CreatePair()
        {
            var a = new InMemoryConnection();
            var b = new InMemoryConnection();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        /// <inheritdoc/>
        public bool IsConnected => Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _peer._closed) == 0;

        /// <inheritdoc/>
        public TransportType Transport => TransportType.Custom;

        /// <inheritdoc/>
        public Task SendAsync(ushort type, byte[] payload, DeliveryMethod delivery, byte channel = 0, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) != 0) return Task.CompletedTask;   // dead end — drop, like a broken socket

            // Copy so neither end can observe the other's buffer mutating after send (mimics a real transport boundary).
            var src = payload ?? Array.Empty<byte>();
            var buffer = new byte[src.Length];
            Buffer.BlockCopy(src, 0, buffer, 0, src.Length);

            _peer._inbound.Write(new TransportMessage(type, buffer));
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async Task<TransportMessage?> ReceiveAsync(CancellationToken ct = default)
        {
            var (ok, message) = await _inbound.ReadAsync(ct).ConfigureAwait(false);
            return ok ? message : (TransportMessage?)null;   // (false, default) => peer closed => EOF
        }

        /// <inheritdoc/>
        public Task FlushAsync() => Task.CompletedTask;

        /// <inheritdoc/>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _inbound.Complete();          // unblock my own read loop
            _peer._inbound.Complete();    // signal EOF to the peer's read loop
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Close();
            _inbound.Dispose();
        }
    }
}
