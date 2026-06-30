using System.Text.Json;
using System.Threading.Tasks;
using MessagePack;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.Messaging;
using SetNet.MessagePack;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// A drop-in <see cref="ISerializer"/> backed by <see cref="System.Text.Json"/> (in-box on net8.0). Proves the
/// library can carry messages over a non-MessagePack wire format chosen entirely by the consumer.
/// </summary>
public sealed class JsonNetSerializer : ISerializer
{
    /// <inheritdoc/>
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    /// <inheritdoc/>
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data)!;
}

/// <summary>
/// Unit-level checks of the pluggable serialization seam. They read the process-wide
/// <see cref="SetNetSerializer.Default"/> (set to MessagePack by the test module initializer), so they join the
/// non-parallel "integration" collection to avoid racing the test that temporarily swaps the default to JSON.
/// </summary>
[Collection("integration")]
public class SerializerUnitTests
{
    [Fact]
    public void Default_Is_MessagePack()
    {
        Assert.IsType<MessagePackNetSerializer>(SetNetSerializer.Default);
    }

    [Fact]
    public void Configuration_FallsBackToGlobalDefault()
    {
        // A fresh Configuration with no explicit serializer reports the global default.
        var config = new Configuration();
        Assert.Same(SetNetSerializer.Default, config.Serializer);
    }

    [Fact]
    public void Configuration_PerConnectionOverride_Wins()
    {
        var custom = new JsonNetSerializer();
        var config = new Configuration { Serializer = custom };
        Assert.Same(custom, config.Serializer);
        Assert.NotSame(SetNetSerializer.Default, config.Serializer);
    }

    [Fact]
    public void Json_Serializer_RoundTrips()
    {
        ISerializer json = new JsonNetSerializer();
        var bytes = json.Serialize(new EchoMessage { Text = "hi" });
        var back = json.Deserialize<EchoMessage>(bytes);
        Assert.Equal("hi", back.Text);
    }

    [Fact]
    public void MessagePackNetSerializer_AppliesUntrustedDataProfile()
    {
        // The adapter must encode with the hardened UntrustedData profile (not plain Standard options),
        // so its bytes match a direct MessagePack call configured the same way.
        ISerializer adapter = new MessagePackNetSerializer();
        var viaAdapter = adapter.Serialize(new EchoMessage { Text = "same" });
        var hardened = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
        var direct = MessagePackSerializer.Serialize(new EchoMessage { Text = "same" }, hardened);
        Assert.Equal(direct, viaAdapter);
    }
}

/// <summary>
/// End-to-end proof that the library carries traffic over a consumer-chosen serializer: with the global
/// <see cref="SetNetSerializer.Default"/> swapped to JSON, a full client → server → client echo round-trips.
/// Lives in the non-parallel "integration" collection and restores the default in a finally block.
/// </summary>
[Collection("integration")]
public class SerializerIntegrationTests
{
    [Fact]
    public async Task CustomSerializer_RoundTripsEndToEnd()
    {
        var original = SetNetSerializer.Default;
        SetNetSerializer.Default = new JsonNetSerializer();
        try
        {
            TestInbox.Reset();
            var config = new Configuration { Host = "127.0.0.1", Port = 5841, TransportType = TransportType.Tcp };
            var server = new TestServer(config);
            _ = server.StartAsync();
            await Task.Delay(200);

            var client = new TestClient(new Configuration
            {
                Host = "127.0.0.1",
                Port = 5841,
                TransportType = TransportType.Tcp
            });
            await client.ConnectAsync();
            await client.SendEchoAsync("json-roundtrip", DeliveryMethod.Reliable);

            // Both ends decode via the JSON serializer through the SetNetSerializer facade in the handlers.
            Assert.True(await WaitUntil(() =>
                TestInbox.ServerReceived.Contains("json-roundtrip") &&
                TestInbox.ClientReceived.Contains("json-roundtrip")));

            client.Disconnect();
            await server.StopAsync();
        }
        finally
        {
            SetNetSerializer.Default = original;
        }
    }

    private static async Task<bool> WaitUntil(System.Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
