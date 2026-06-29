using System.Linq;
using SetNet.Core;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the TCP length-prefix framing and stream reassembly in <see cref="PacketBuilder"/>.</summary>
public class PacketBuilderTests
{
    [Fact]
    public void BuildAndParse_RoundTrips()
    {
        var pb = new PacketBuilder();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var wire = pb.BuildPacket(42, payload);

        var rx = new PacketBuilder();
        rx.AppendData(wire);
        Assert.True(rx.TryGetCompletePacket(out var frame));
        var (type, data) = PacketBuilder.ParsePacket(frame);
        Assert.Equal((ushort)42, type);
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Reassembles_PacketSplitAcrossReads()
    {
        // 5000 bytes guarantees the packet spans multiple "reads" — the regression case that used to corrupt.
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var wire = new PacketBuilder().BuildPacket(7, payload);

        var rx = new PacketBuilder();
        rx.AppendData(wire, 0, 2000);
        Assert.False(rx.TryGetCompletePacket(out _)); // incomplete after the first chunk
        rx.AppendData(wire, 2000, wire.Length - 2000);

        Assert.True(rx.TryGetCompletePacket(out var frame));
        var (type, data) = PacketBuilder.ParsePacket(frame);
        Assert.Equal((ushort)7, type);
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Drains_MultiplePacketsFromOneAppend()
    {
        var pb = new PacketBuilder();
        var p1 = pb.BuildPacket(1, new byte[] { 10 });
        var p2 = pb.BuildPacket(2, new byte[] { 20, 21 });

        var rx = new PacketBuilder();
        rx.AppendData(p1.Concat(p2).ToArray());

        Assert.True(rx.TryGetCompletePacket(out var f1));
        Assert.True(rx.TryGetCompletePacket(out var f2));
        Assert.False(rx.TryGetCompletePacket(out _));
        Assert.Equal((ushort)1, PacketBuilder.ParsePacket(f1).Item1);
        Assert.Equal((ushort)2, PacketBuilder.ParsePacket(f2).Item1);
    }

    [Fact]
    public void NegativeLengthPrefix_ReturnsFalse_DoesNotThrow()
    {
        var rx = new PacketBuilder();
        // length prefix = 0xFFFFFFFF (-1) followed by a few bytes — must be rejected, not crash.
        rx.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 });
        Assert.False(rx.TryGetCompletePacket(out _));
    }

    [Fact]
    public void WriteFrame_IntoOversizedBuffer_ReturnsExactLength()
    {
        var payload = new byte[] { 7, 7, 7 };
        var dest = new byte[256]; // larger than needed (simulating a pooled buffer)

        var total = PacketBuilder.WriteFrame(dest, 5, payload, payload.Length);

        Assert.Equal(PacketBuilder.HeaderSize + payload.Length, total);
        var rx = new PacketBuilder();
        rx.AppendData(dest, 0, total);
        Assert.True(rx.TryGetCompletePacket(out var frame));
        Assert.Equal((ushort)5, PacketBuilder.ParsePacket(frame).Item1);
    }
}
