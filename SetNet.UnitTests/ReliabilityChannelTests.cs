using System;
using System.Collections.Generic;
using System.Linq;
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
    public async Task OrderedBeyondWindow_IsRefused_NotBuffered()
    {
        // Regression (deterministic): a future packet BEYOND the receive window [base, base+window) must be
        // refused, not buffered — otherwise the high-water mark runs past the gap (out of the 64-bit ACK window).
        // We verify via delivery: with a window of 4 and seq 0 missing, seqs 1,2,3 are buffered but seq 4 is
        // refused; once seq 0 arrives only 0..3 are delivered (seq 4 was never retained).
        var config = new Configuration
        {
            Host = "127.0.0.1", Port = 1, TransportType = TransportType.Udp,
            UdpReliabilityEnabled = true, UdpOrderedReliable = true,
            UdpReliableWindowSize = 4 // receive window [0,4)
        };

        var inbound = new AsyncQueue<TransportMessage>();
        using var receiver = new ReliabilityChannel(config, 0, (_, _) => Task.CompletedTask, inbound);

        // seq 0 "lost" initially: feed 1,2,3 (within window -> buffered), 4 (beyond window -> refused), then 0.
        for (ushort s = 1; s <= 4; s++) receiver.OnReliableDatagram(UdpDatagram.BuildReliable(0, s, 100, new[] { (byte)s }));
        receiver.OnReliableDatagram(UdpDatagram.BuildReliable(0, 0, 100, new byte[] { 0 }));

        // seq 0 arrival drains the buffered run synchronously: expect exactly 0,1,2,3 (seq 4 was refused).
        var got = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 5; i++)
        {
            var m = await Dequeue(inbound, 200);
            if (m == null) break;
            got.Add(m.Value.Payload[0]);
        }
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, got); // seq 4 (beyond the window) was refused, never delivered
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleLoss_WithManyFurtherSends_DeliversAllAndDoesNotFail(bool ordered)
    {
        // Regression: a single lost packet plus far more than 64 further reliable sends used to push the
        // high-water mark past the gap (out of the 64-bit ACK window), causing permanent loss (unordered) or a
        // spurious onFailure teardown (ordered). With the bounded receive window the gap stays ackable; the
        // dropped packet is retransmitted and every message is delivered exactly once, with no failure.
        const int n = 300; // well past the 64-sequence window
        var cfg = new Configuration
        {
            Host = "127.0.0.1", Port = 1, TransportType = TransportType.Udp,
            UdpReliabilityEnabled = true, UdpOrderedReliable = ordered,
            UdpReliableWindowSize = 64, UdpReliableAckTimeoutMs = 20, UdpReliableMaxRetransmits = 1000
        };

        var inbound = new AsyncQueue<TransportMessage>();
        ReliabilityChannel sender = null!, receiver = null!;
        var failed = false;
        var dropFirst = 1; // drop exactly the first datagram the sender emits (seq 0's first transmission)

        sender = new ReliabilityChannel(cfg, 0,
            (buf, count) => { if (System.Threading.Interlocked.Exchange(ref dropFirst, 0) == 0) receiver.OnReliableDatagram(buf[..count]); return Task.CompletedTask; },
            new AsyncQueue<TransportMessage>(), onFailure: () => failed = true);
        receiver = new ReliabilityChannel(cfg, 0,
            (buf, count) => { sender.OnAck(buf[..count]); return Task.CompletedTask; },
            inbound, onFailure: () => failed = true);

        var received = new System.Collections.Generic.List<int>();
        var consumer = Task.Run(async () =>
        {
            while (received.Count < n)
            {
                using var c = new CancellationTokenSource(8000);
                var (ok, m) = await inbound.DequeueAsync(c.Token);
                if (!ok) break;
                received.Add(BitConverter.ToInt32(m.Payload, 0));
            }
        });

        for (int i = 0; i < n; i++)
            await sender.SendAsync(100, BitConverter.GetBytes(i));

        try { await consumer; } catch (OperationCanceledException) { }
        sender.Dispose(); receiver.Dispose();

        Assert.False(failed, "a healthy link was torn down by a spurious onFailure");
        Assert.Equal(n, received.Count);
        if (ordered)
            for (int i = 0; i < n; i++) Assert.Equal(i, received[i]); // exact in-order delivery
        else
            Assert.Equal(Enumerable.Range(0, n), received.OrderBy(x => x)); // all delivered exactly once
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
