using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Commands;
using SetNet.Core.Transport;
using SetNet.Data;

namespace SetNet.Config
{
    /// <summary>
    /// Server-side record describing a single connected client: its identity, the transport channel(s) it owns, the
    /// shared <see cref="Configuration"/>, and the command executor that dispatches its inbound messages. It is the
    /// handle the server uses to talk to and tear down one peer.
    /// </summary>
    public class PeerInfo
    {
        /// <summary>Primary connection (TCP in Tcp/Both modes; UDP in Udp mode).</summary>
        public ITransportConnection Connection;

        /// <summary>Secondary UDP connection, attached in Both mode after the UDP handshake binds.</summary>
        public volatile ITransportConnection? UdpConnection;

        /// <summary>Server-assigned unique identifier for this peer, generated when the record is created.</summary>
        public Guid Id;

        /// <summary>
        /// The remote endpoint (IP + port) this peer connected from, when the transport exposes it (TCP and UDP do; an
        /// in-memory transport may not). Exposed for companion packages that key on address — ban lists, DDoS guards,
        /// diagnostics dashboards, gateways. May be <c>null</c>.
        /// </summary>
        public System.Net.IPEndPoint? RemoteEndPoint;

        /// <summary>The configuration shared with the owning server, used for buffering, transport, and timing settings.</summary>
        public Configuration Config;

        /// <summary>Back-reference to the owning server, used to deregister this peer from the connected-client pool on disconnect.</summary>
        private BaseServer _server;

        /// <summary>The server that accepted this peer. Exposed so companion packages (e.g. auth) can reach server-level hooks such as <see cref="BaseServer.InboundAuthorizer"/>.</summary>
        public BaseServer Server => _server;

        /// <summary>The command executor that routes this peer's inbound messages to their registered server-side handlers.</summary>
        public readonly ServerCommandExecutor CommandExecutor;

        /// <summary>0 while the peer is live, 1 once <see cref="Disconnect"/> has been requested.</summary>
        private int _disconnected;

        /// <summary>True once this peer has disconnected, even if the server had not registered it in the pool yet.</summary>
        internal bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

        /// <summary>
        /// Creates a peer record for a newly accepted client, capturing its transport, shared configuration, owning
        /// server, and message dispatcher, and assigning it a fresh unique <see cref="Id"/>.
        /// </summary>
        /// <param name="connection">The primary transport connection to the client.</param>
        /// <param name="config">The shared configuration governing this peer's behaviour.</param>
        /// <param name="server">The server that owns this peer, used to remove it on disconnect.</param>
        /// <param name="commandExecutor">The executor that dispatches the peer's inbound messages to handlers.</param>
        /// <param name="remoteEndPoint">The client's remote endpoint, if the transport exposes it; otherwise <c>null</c>.</param>
        public PeerInfo(ITransportConnection connection, Configuration config, BaseServer server, ServerCommandExecutor commandExecutor, System.Net.IPEndPoint? remoteEndPoint = null)
        {
            Connection = connection;
            Config = config;
            _server = server;
            CommandExecutor = commandExecutor;
            RemoteEndPoint = remoteEndPoint;
            Id = Guid.NewGuid();
        }

        /// <summary>
        /// Disconnects this peer by closing its primary and any secondary UDP channel, then removing it from the
        /// server's connected-client pool so it is no longer tracked or dispatched to.
        /// </summary>
        public void Disconnect()
        {
            Interlocked.Exchange(ref _disconnected, 1);
            Connection.Close();
            UdpConnection?.Close();
            _server?.RemoveClient(this);
        }

        /// <summary>
        /// Tells the client this close is <b>deliberate and final</b> and then disconnects it. Use for bans,
        /// geo-blocks, a session displaced by a newer login, or a protocol violation — anything the client should not
        /// come straight back from.
        /// </summary>
        /// <param name="reason">Optional human-readable reason handed to the client's <c>OnKicked</c> hook.</param>
        /// <remarks>
        /// A plain <see cref="Disconnect"/> is indistinguishable from a crash on the wire, so a client with
        /// <see cref="Configuration.AutoReconnect"/> reconnects into the same kick. This sends the reserved
        /// <c>Kick</c> frame first (best-effort, and flushed so batching cannot hold it back), which suppresses the
        /// client's reconnect for that teardown. Fire-and-forget: the disconnect happens once the notice is on the
        /// wire, so callers on an event handler do not block.
        /// </remarks>
        public void Kick(string? reason = null) => _ = KickAsync(reason);

        /// <summary>Awaitable form of <see cref="Kick"/>, completing once the notice is sent and the peer is disconnected.</summary>
        /// <param name="reason">Optional human-readable reason handed to the client's <c>OnKicked</c> hook.</param>
        /// <returns>A task that completes after the peer has been disconnected.</returns>
        public async Task KickAsync(string? reason = null)
        {
            if (!IsDisconnected)
            {
                var payload = string.IsNullOrEmpty(reason)
                    ? Array.Empty<byte>()
                    : System.Text.Encoding.UTF8.GetBytes(reason!);
                try
                {
                    await SendKickAsync(payload, DeliveryMethod.Reliable).ConfigureAwait(false);
                }
                catch
                {
                    // Reliable is unavailable on a UDP transport with the reliability layer off; a best-effort
                    // datagram still spares the client a pointless reconnect in the common case.
                    try { await SendKickAsync(payload, DeliveryMethod.Unreliable).ConfigureAwait(false); }
                    catch { /* link already gone; the disconnect below is what matters */ }
                }
            }

            Disconnect();
        }

        /// <summary>Sends the kick notice over the primary connection and flushes it past any send batching.</summary>
        private async Task SendKickAsync(byte[] payload, DeliveryMethod delivery)
        {
            await Connection.SendAsync(SystemMessageTypes.Kick, payload, delivery).ConfigureAwait(false);
            await Connection.FlushAsync().ConfigureAwait(false);
        }
    }
}
