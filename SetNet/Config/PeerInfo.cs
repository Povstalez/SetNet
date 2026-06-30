using System;
using System.Threading;
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

        /// <summary>The configuration shared with the owning server, used for buffering, transport, and timing settings.</summary>
        public Configuration Config;

        /// <summary>Back-reference to the owning server, used to deregister this peer from the connected-client pool on disconnect.</summary>
        private BaseServer _server;

        /// <summary>The command executor that routes this peer's inbound messages to their registered server-side handlers.</summary>
        public readonly CommandExecutor<IServerMessageHandler> CommandExecutor;

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
        public PeerInfo(ITransportConnection connection, Configuration config, BaseServer server, CommandExecutor<IServerMessageHandler> commandExecutor)
        {
            Connection = connection;
            Config = config;
            _server = server;
            CommandExecutor = commandExecutor;
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
    }
}
