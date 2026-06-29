using System;
using SetNet.Config;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for <see cref="Configuration.Validate"/> and derived settings.</summary>
public class ConfigurationTests
{
    [Fact]
    public void Validate_DefaultTcp_Passes()
        => new Configuration { Host = "127.0.0.1", Port = 5000 }.Validate();

    [Fact]
    public void Validate_NullHost_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => new Configuration { Host = null!, Port = 5000 }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Validate_BadPort_Throws(int port)
        => Assert.Throws<InvalidOperationException>(
            () => new Configuration { Host = "127.0.0.1", Port = port }.Validate());

    [Fact]
    public void Validate_UdpWindowAbove64_Throws()
    {
        var config = new Configuration
        {
            Host = "127.0.0.1", Port = 5000,
            TransportType = TransportType.Udp, UdpReliableWindowSize = 128
        };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_UdpWindowBound_DoesNotApplyToTcp()
    {
        // A nonsensical UDP window must NOT fail validation when the transport is plain TCP.
        new Configuration
        {
            Host = "127.0.0.1", Port = 5000,
            TransportType = TransportType.Tcp, UdpReliableWindowSize = 128
        }.Validate();
    }

    [Fact]
    public void EffectiveUdpPort_FallsBackToPortWhenZero()
    {
        Assert.Equal(5000, new Configuration { Port = 5000, UdpPort = 0 }.EffectiveUdpPort);
        Assert.Equal(6000, new Configuration { Port = 5000, UdpPort = 6000 }.EffectiveUdpPort);
    }
}
