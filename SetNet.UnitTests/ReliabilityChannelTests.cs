using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Core.Transport.Udp;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>
/// Unit tests for the reliability layer's deterministic, synchronous behaviour: ordered delivery,
/// duplicate suppression, and reliable framing. (Retransmit/ack timing is covered end-to-end by the
/// integration loss test.)
/// </summary>
public class ReliabilityChannelTests
{
    private static Configuration Config(bool ordered) => new Configuration
    {
        Host = "127.0.0.1", Port = 1, TransportType = TransportType.Udp,
        UdpReliabilityEnabled = true, UdpOrderedReliable = ordered,
        UdpReliableWindowSize = 64, UdpReliableAckTimeoutMs = 1000
    };

    [Fact]
    public async Task OrderedDelivery_ReordersOutOfOrderPackets()
    {
        var inbound = new AsyncQueue<TransportMessage>();
        using var channel = new ReliabilityChannel(Config(ordered: true), 0, (_, _) => Task.CompletedTask, inbound);

        // Receive seq 1 before seq 0 — ordered mode must hold 1 back until 0 arrives.
        channel.OnReliableDatagram(UdpDatagram.BuildReliable(0, 1, 100, new byte[] { 1 }));
        channel.OnReliableDatagram(UdpDatagram.BuildReliable(0, 0, 100, new byte[] { 0 }));

        var first = await Dequeue(inbound);
        var second = await Dequeue(inbound);
        Assert.Equal(new byte[] { 0 }, first!.Value.Payload);
        Assert.Equal(new byte[] { 1 }, second!.Value.Payload);
    }

    [Fact]
    public async Task DuplicateSeq_IsDeliveredOnce()
    {
        var inbound = new AsyncQueue<TransportMessage>();
        using var channel = new ReliabilityChannel(Config(ordered: false), 0, (_, _) => Task.CompletedTask, inbound);

        channel.OnReliableDatagram(UdpDatagram.BuildReliable(0, 0, 100, new byte[] { 7 }));
        channel.OnReliableDatagram(UdpDatagram.BuildReliable(0, 0, 100, new byte[] { 7 })); // duplicate

        Assert.NotNull(await Dequeue(inbound));
        Assert.Null(await Dequeue(inbound, 200)); // no second delivery
    }

    [Fact]
    public async Task SendAsync_EmitsReliableDatagram()
    {
        var inbound = new AsyncQueue<TransportMessage>();
        var sent = new List<byte[]>();
        using var channel = new ReliabilityChannel(
            Config(ordered: true),
            0,
            (buffer, count) => { sent.Add(buffer[..count]); return Task.CompletedTask; },
            inbound);

        await channel.SendAsync(100, new byte[] { 1, 2, 3 });

        Assert.Single(sent);
        Assert.Equal(PacketKind.Reliable, sent[0][0]);
    }

    private static async Task<TransportMessage?> Dequeue(AsyncQueue<TransportMessage> queue, int timeoutMs = 1000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var (ok, item) = await queue.DequeueAsync(cts.Token);
            return ok ? item : (TransportMessage?)null;
        }
        catch (OperationCanceledException) { return null; }
    }
}
