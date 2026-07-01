using System;
using SetNet.Core.Transport.Udp;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the UDP wire-format build/parse helpers in <see cref="UdpDatagram"/>.</summary>
public class UdpDatagramTests
{
    [Fact]
    public void Token_RoundTrips()
    {
        var token = Guid.NewGuid();
        var dg = UdpDatagram.BuildToken(PacketKind.Handshake, token);

        Assert.Equal(PacketKind.Handshake, dg[0]);
        Assert.True(UdpDatagram.TryParseToken(dg, out var parsed));
        Assert.Equal(token, parsed);
    }

    [Fact]
    public void Unreliable_RoundTrips()
    {
        var payload = new byte[] { 9, 8, 7 };
        var dest = new byte[64];

        var len = UdpDatagram.WriteUnreliable(dest, 1234, payload);

        Assert.True(UdpDatagram.TryParseUnreliable(dest[..len], out var type, out var body));
        Assert.Equal((ushort)1234, type);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void Reliable_RoundTrips()
    {
        var payload = new byte[] { 5, 5, 5, 5 };
        var dg = UdpDatagram.BuildReliable(2, 77, 4321, payload);

        Assert.True(UdpDatagram.TryParseReliable(dg, out var channel, out var seq, out var type, out var body));
        Assert.Equal((byte)2, channel);
        Assert.Equal((ushort)77, seq);
        Assert.Equal((ushort)4321, type);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void Ack_RoundTrips()
    {
        var dest = new byte[UdpDatagram.AckSize];
        const ulong bitfield = 0xDEADBEEF12345678UL;

        UdpDatagram.WriteAck(dest, 3, 999, bitfield);

        Assert.True(UdpDatagram.TryParseAck(dest, out var channel, out var ackSeq, out var bf));
        Assert.Equal((byte)3, channel);
        Assert.Equal((ushort)999, ackSeq);
        Assert.Equal(bitfield, bf);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TryParseUnreliable_TooShort_ReturnsFalse(int len)
        => Assert.False(UdpDatagram.TryParseUnreliable(new byte[len], out _, out _));

    [Fact]
    public void TryParseToken_TooShort_ReturnsFalse()
        => Assert.False(UdpDatagram.TryParseToken(new byte[5], out _));
}
