using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Client-side UDP dialer. Performs the 2-way emulated-connection handshake
    /// (SYN 0x01 → SYN-ACK 0x02) with retransmission and a timeout, then hands the
    /// socket to a <see cref="UdpClientConnection"/>.
    /// </summary>
    internal sealed class UdpClientConnector : ITransportConnector
    {
        /// <summary>
        /// Connects to the configured UDP endpoint by performing the emulated-connection handshake with a
        /// freshly generated session token. This is the standard entry point used when the client is the
        /// one establishing the UDP flow (UDP-only mode).
        /// </summary>
        /// <param name="config">Connection configuration providing the host, UDP port, and handshake timing.</param>
        /// <param name="ct">Token to cancel the connection attempt while handshaking.</param>
        /// <returns>A connected <see cref="ITransportConnection"/> ready to send and receive once the handshake is acknowledged.</returns>
        /// <exception cref="TimeoutException">Thrown if no valid handshake acknowledgement arrives within <see cref="Configuration.UdpHandshakeTimeoutMs"/>.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="ct"/> is cancelled during the handshake.</exception>
        public async Task<ITransportConnection> ConnectAsync(Configuration config, CancellationToken ct = default)
            => await ConnectWithTokenAsync(config, Guid.NewGuid(), ct);

        /// <summary>
        /// Handshake using a specific token. In Both mode this is the server-issued token (received
        /// over TCP) so the server can bind the UDP flow to the existing TCP peer.
        /// </summary>
        /// <param name="config">Connection configuration providing the host, UDP port, and handshake timing (timeout and retransmit interval).</param>
        /// <param name="token">The session token to present in the handshake; pre-supplied by the server in Both mode, otherwise freshly generated.</param>
        /// <param name="ct">Token to cancel the connection attempt while handshaking.</param>
        /// <returns>A connected <see cref="UdpClientConnection"/> bound to the supplied token once the server acknowledges it.</returns>
        /// <exception cref="TimeoutException">Thrown if no matching handshake acknowledgement arrives within <see cref="Configuration.UdpHandshakeTimeoutMs"/>.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="ct"/> is cancelled during the handshake.</exception>
        /// <remarks>
        /// Implements a SYN/SYN-ACK exchange: the handshake datagram is (re)sent every
        /// <see cref="Configuration.UdpHandshakeRetransmitMs"/> until either a matching
        /// <see cref="PacketKind.HandshakeAck"/> echoing <paramref name="token"/> arrives or the overall
        /// timeout elapses. Datagrams that are not the expected ack are ignored and the wait continues.
        /// On any failure the socket is closed before the exception propagates.
        /// </remarks>
        public static async Task<UdpClientConnection> ConnectWithTokenAsync(Configuration config, Guid token, CancellationToken ct = default)
        {
            var udp = new UdpClient();
            udp.Connect(config.Host, config.EffectiveUdpPort);

            var handshake = UdpDatagram.BuildToken(PacketKind.Handshake, token);

            try
            {
                var sw = Stopwatch.StartNew();
                await udp.SendAsync(handshake, handshake.Length).ConfigureAwait(false);
                var recvTask = udp.ReceiveAsync();

                while (sw.ElapsedMilliseconds < config.UdpHandshakeTimeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    var completed = await Task.WhenAny(recvTask, Task.Delay(config.UdpHandshakeRetransmitMs, ct)).ConfigureAwait(false);
                    if (completed == recvTask)
                    {
                        var result = await recvTask.ConfigureAwait(false);
                        var dg = result.Buffer;
                        if (dg.Length >= 1 && dg[0] == PacketKind.HandshakeAck &&
                            UdpDatagram.TryParseToken(dg, out var ackToken) && ackToken == token)
                        {
                            return new UdpClientConnection(udp, config, token);
                        }

                        recvTask = udp.ReceiveAsync(); // unexpected datagram; keep waiting
                    }
                    else
                    {
                        await udp.SendAsync(handshake, handshake.Length).ConfigureAwait(false); // retransmit SYN
                    }
                }

                throw new TimeoutException($"UDP handshake timed out after {config.UdpHandshakeTimeoutMs}ms");
            }
            catch
            {
                try { udp.Close(); } catch { }
                throw;
            }
        }
    }
}
