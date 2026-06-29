using System;
using System.IO;
using SetNet.Core;
using SetNet.Core.Transport.Udp;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>
/// Fuzz tests: feed random/malformed bytes through the frame and datagram parsers and assert they never
/// crash the receive path (return false / throw only the documented oversize error), and verify the
/// frame-size cap rejects oversized declared lengths.
/// </summary>
public class FuzzTests
{
    [Fact]
    public void PacketBuilder_RandomBytes_NeverThrow()
    {
        var rng = new Random(1234567);
        var pb = new PacketBuilder(); // no frame cap ⇒ any throw is a bug

        for (int i = 0; i < 3000; i++)
        {
            var chunk = new byte[rng.Next(0, 64)];
            rng.NextBytes(chunk);
            pb.AppendData(chunk);
            while (pb.TryGetCompletePacket(out _)) { /* drain whatever the garbage parsed into */ }
        }
    }

    [Fact]
    public void UdpDatagram_RandomBytes_TryParseNeverThrows()
    {
        var rng = new Random(7654321);

        for (int i = 0; i < 5000; i++)
        {
            var dg = new byte[rng.Next(0, 40)];
            rng.NextBytes(dg);

            UdpDatagram.TryParseToken(dg, out _);
            UdpDatagram.TryParseUnreliable(dg, out _, out _);
            UdpDatagram.TryParseReliable(dg, out _, out _, out _, out _);
            UdpDatagram.TryParseAck(dg, out _, out _, out _);
        }
    }

    [Fact]
    public void PacketBuilder_FrameAboveMaxSize_Throws()
    {
        var pb = new PacketBuilder(maxFrameSize: 100);

        // A length-prefix declaring 1000 bytes (> 100) must be rejected immediately, not buffered.
        pb.AppendData(BitConverter.GetBytes(1000));

        Assert.Throws<InvalidDataException>(() => pb.TryGetCompletePacket(out _));
    }

    [Fact]
    public void PacketBuilder_FrameWithinMaxSize_IsAccepted()
    {
        var pb = new PacketBuilder(maxFrameSize: 4096);
        var wire = new PacketBuilder().BuildPacket(3, new byte[] { 1, 2, 3 });

        pb.AppendData(wire);

        Assert.True(pb.TryGetCompletePacket(out var frame));
        Assert.Equal((ushort)3, PacketBuilder.ParsePacket(frame).Item1);
    }
}
