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
    public void Validate_UdpReliableDefault_WithReliabilityDisabled_Throws()
    {
        // Regression: this misconfiguration used to pass Validate() and only throw on the first send. It must
        // now fail fast at connect/start time.
        var config = new Configuration
        {
            Host = "127.0.0.1", Port = 5000,
            TransportType = TransportType.Udp,
            UdpReliabilityEnabled = false,
            DefaultDelivery = DeliveryMethod.Reliable
        };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_UdpUnreliableDefault_WithReliabilityDisabled_Passes()
    {
        // The same transport is fine when the default delivery is Unreliable (no reliable channel needed).
        new Configuration
        {
            Host = "127.0.0.1", Port = 5000,
            TransportType = TransportType.Udp,
            UdpReliabilityEnabled = false,
            DefaultDelivery = DeliveryMethod.Unreliable
        }.Validate();
    }

    [Fact]
    public void Validate_BothReliableDefault_WithUdpReliabilityDisabled_Passes()
    {
        // In Both mode reliable rides the TCP channel, so disabling UDP reliability is not a conflict.
        new Configuration
        {
            Host = "127.0.0.1", Port = 5000,
            TransportType = TransportType.Both,
            UdpReliabilityEnabled = false,
            DefaultDelivery = DeliveryMethod.Reliable
        }.Validate();
    }

    [Fact]
    public void EffectiveUdpPort_FallsBackToPortWhenZero()
    {
        Assert.Equal(5000, new Configuration { Port = 5000, UdpPort = 0 }.EffectiveUdpPort);
        Assert.Equal(6000, new Configuration { Port = 5000, UdpPort = 6000 }.EffectiveUdpPort);
    }
}
