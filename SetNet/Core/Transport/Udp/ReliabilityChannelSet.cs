using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;

namespace SetNet.Core.Transport.Udp
{
    /// <summary>
    /// Owns one <see cref="ReliabilityChannel"/> per configured reliable channel and routes reliable sends, ACKs,
    /// and inbound reliable datagrams to the right channel by its channel byte. Independent channels mean a loss
    /// on one (e.g. chat) does not head-of-line block another (e.g. movement). Created empty when reliability is off.
    /// </summary>
    internal sealed class ReliabilityChannelSet : IDisposable
    {
        /// <summary>One reliability channel per id; empty when <see cref="Configuration.UdpReliabilityEnabled"/> is off.</summary>
        private readonly ReliabilityChannel[] _channels;

        /// <summary>True when at least one reliable channel exists (reliability enabled).</summary>
        public bool Enabled => _channels.Length > 0;

        /// <summary>Builds the set of reliable channels (0..<see cref="Configuration.UdpReliableChannels"/>-1), or none when disabled.</summary>
        /// <param name="config">Reliability/channel configuration.</param>
        /// <param name="sendRaw">Egress callback shared by every channel (buffer, length).</param>
        /// <param name="inbound">Inbound queue every channel delivers ordered/deduped messages into.</param>
        /// <param name="onFailure">Failure callback invoked when any channel exhausts its retransmit budget.</param>
        /// <param name="enabled">When false, no channels are built regardless of config — used for the Both-mode server UDP leg, which carries only unreliable traffic (reliable rides TCP), so its reliability machinery would be pure overhead.</param>
        public ReliabilityChannelSet(Configuration config, Func<byte[], int, Task> sendRaw, AsyncQueue<TransportMessage> inbound, Action onFailure, bool enabled = true)
        {
            if (!enabled || !config.UdpReliabilityEnabled)
            {
                _channels = Array.Empty<ReliabilityChannel>();
                return;
            }

            var count = config.UdpReliableChannels;
            _channels = new ReliabilityChannel[count];
            for (int i = 0; i < count; i++)
                _channels[i] = new ReliabilityChannel(config, (byte)i, sendRaw, inbound, onFailure);
        }

        /// <summary>Sends a message reliably on the given channel.</summary>
        /// <param name="channel">Target reliable channel id.</param>
        /// <param name="type">Application message type.</param>
        /// <param name="payload">Serialized payload.</param>
        /// <param name="ct">Cancellation token for the window wait.</param>
        /// <returns>A task that completes once the packet is recorded and first-sent.</returns>
        /// <exception cref="InvalidOperationException">Reliability is disabled.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a configured channel.</exception>
        public Task SendAsync(byte channel, ushort type, byte[] payload, CancellationToken ct)
        {
            if (!Enabled)
                throw new InvalidOperationException("Reliable delivery over UDP requires Configuration.UdpReliabilityEnabled = true.");
            if (channel >= _channels.Length)
                throw new ArgumentOutOfRangeException(nameof(channel),
                    $"Channel {channel} is out of range; Configuration.UdpReliableChannels = {_channels.Length}.");
            return _channels[channel].SendAsync(type, payload, ct);
        }

        /// <summary>Routes an inbound reliable datagram to the channel named by its channel byte.</summary>
        /// <param name="dg">The reliable datagram.</param>
        public void OnReliableDatagram(byte[] dg)
        {
            var channel = UdpDatagram.GetChannel(dg);
            if (channel < _channels.Length) _channels[channel].OnReliableDatagram(dg);
        }

        /// <summary>Routes an inbound ACK datagram to the channel named by its channel byte.</summary>
        /// <param name="dg">The ack datagram.</param>
        public void OnAck(byte[] dg)
        {
            var channel = UdpDatagram.GetChannel(dg);
            if (channel < _channels.Length) _channels[channel].OnAck(dg);
        }

        /// <summary>Disposes every channel.</summary>
        public void Dispose()
        {
            foreach (var channel in _channels) channel.Dispose();
        }
    }
}
