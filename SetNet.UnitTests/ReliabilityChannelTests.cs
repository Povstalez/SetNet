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

    [Fact]
    public async Task OversizedReliableSend_DoesNotConsumeSequence_OrderedStreamSurvives()
    {
        // Regression: an oversized reliable send used to consume a sequence number BEFORE the size check, leaving
        // a permanent hole the ordered receiver blocks on forever. The next valid send must still arrive in order.
        var senderConfig = Config(ordered: true);
        senderConfig.UdpMaxDatagramPayload = 32; // header is 6B, so any payload > 26B is oversized

        var inbound = new AsyncQueue<TransportMessage>();
        ReliabilityChannel receiver = null!;
        using var sender = new ReliabilityChannel(senderConfig, 0,
            (buf, count) => { receiver.OnReliableDatagram(buf[..count]); return Task.CompletedTask; },
            new AsyncQueue<TransportMessage>());
        receiver = new ReliabilityChannel(Config(ordered: true), 0, (_, _) => Task.CompletedTask, inbound);

        await sender.SendAsync(100, new byte[] { 1 });                                   // seq 0 -> delivered
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sender.SendAsync(100, new byte[64]));                                  // oversized -> throws
        await sender.SendAsync(100, new byte[] { 2 });                                   // must be seq 1, not seq 2

        Assert.Equal(new byte[] { 1 }, (await Dequeue(inbound))!.Value.Payload);
        Assert.Equal(new byte[] { 2 }, (await Dequeue(inbound))!.Value.Payload);         // arrives only if no gap
        receiver.Dispose();
    }

    [Fact]
    public async Task OrderedReorderOverflow_DoesNotAckDroppedSequence()
    {
        // Regression: a future packet dropped because the reorder buffer is full must NOT be acknowledged —
        // otherwise the sender stops retransmitting it and the ordered stream wedges permanently. The emitted
        // cumulative ACK must cover only the retained sequences, not the dropped one.
        var config = new Configuration
        {
            Host = "127.0.0.1", Port = 1, TransportType = TransportType.Udp,
            UdpReliabilityEnabled = true, UdpOrderedReliable = true,
            UdpReliableWindowSize = 2,        // reorder cap = window * 2 = 4
            UdpReliableAckTimeoutMs = 20      // fast ack tick
        };

        ushort lastAckSeq = 0;
        var gotAck = false;
        using var receiver = new ReliabilityChannel(config, 0,
            (buf, count) => { if (UdpDatagram.TryParseAck(buf[..count], out _, out lastAckSeq, out _)) gotAck = true; return Task.CompletedTask; },
            new AsyncQueue<TransportMessage>());

        // seq 0 is "lost"; feed future seqs 1..4 (buffered, reorder fills to cap) then 5 (dropped: reorder full).
        for (ushort s = 1; s <= 4; s++) receiver.OnReliableDatagram(UdpDatagram.BuildReliable(0, s, 100, new[] { (byte)s }));
        receiver.OnReliableDatagram(UdpDatagram.BuildReliable(0, 5, 100, new byte[] { 5 }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!gotAck && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Assert.True(gotAck, "receiver never emitted an ACK");
        Assert.Equal((ushort)4, lastAckSeq); // cumulative ACK covers the buffered 1..4, NOT the dropped 5
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
