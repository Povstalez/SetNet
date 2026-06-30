using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Process-wide capture for the raw-frame / relay test.</summary>
public static class RelayProbe
{
    /// <summary>Set true if the typed server handler for the relayed type ran (it must NOT, since OnRawFrame consumes it).</summary>
    public static int ServerTypedRan;

    /// <summary>The raw bytes the relay peer intercepted in OnRawFrame.</summary>
    public static volatile byte[]? InterceptedRaw;

    /// <summary>Texts the client received via the raw-forwarded (relayed) path.</summary>
    public static readonly ConcurrentQueue<string> ClientReceived = new();

    /// <summary>Resets all capture state before a test.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref ServerTypedRan, 0);
        InterceptedRaw = null;
        while (ClientReceived.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Typed server handler for the relayed type (950). It records that it ran — the relay test asserts it does
/// NOT, because the peer's <c>OnRawFrame</c> consumes the frame before typed dispatch.
/// </summary>
[MessageHandler(950)]
public class RelayProbeServerHandler : IServerMessageHandler<EchoMessage>
{
    /// <inheritdoc/>
    public Task HandleAsync(BasePeer peer, EchoMessage message)
    {
        Interlocked.Exchange(ref RelayProbe.ServerTypedRan, 1);
        return Task.CompletedTask;
    }
}

/// <summary>Typed client handler for the relay-output type (951): records the text the relay forwarded back.</summary>
[MessageHandler(951)]
public class RelayOutClientHandler : IClientMessageHandler<EchoMessage>
{
    /// <inheritdoc/>
    public Task HandleAsync(EchoMessage message)
    {
        RelayProbe.ClientReceived.Enqueue(message.Text);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Server peer that relays: intercepts raw frames of type 950 in <see cref="BaseSocket.OnRawFrame"/>, forwards
/// the bytes straight back to the sender as type 951 via <c>SendRawAsync</c> (no deserialize/reserialize), and
/// consumes the frame so the typed 950 handler never runs.
/// </summary>
public class RelayTestPeer : BasePeer
{
    /// <summary>Creates the relay peer.</summary>
    public RelayTestPeer(PeerInfo info) : base(info) { }

    /// <inheritdoc/>
    protected override bool OnRawFrame(ushort type, byte[] data)
    {
        if (type != 950) return false;            // let everything else dispatch normally
        RelayProbe.InterceptedRaw = data;
        _ = SendRawAsync(951, data, DeliveryMethod.Reliable);  // forward raw bytes, no serialization
        return true;                               // consumed: skip typed dispatch
    }

    /// <inheritdoc/>
    protected override void OnDisconnected() { }

    /// <inheritdoc/>
    protected override void OnError(string error) { }
}

/// <summary>Server that creates <see cref="RelayTestPeer"/>s.</summary>
public class RelayTestServer : BaseServer
{
    /// <summary>Creates the relay server.</summary>
    public RelayTestServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new RelayTestPeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

/// <summary>
/// A peer whose <see cref="BaseSocket.OnRawFrame"/> throws for type 952 (to verify the hook is isolated and a
/// throw cannot tear down the receive loop). Extends <see cref="TestPeer"/> so the echo handler's cast works.
/// </summary>
public class ThrowingRawPeer : TestPeer
{
    /// <summary>Creates the throwing peer.</summary>
    public ThrowingRawPeer(PeerInfo info) : base(info) { }

    /// <inheritdoc/>
    protected override bool OnRawFrame(ushort type, byte[] data)
    {
        if (type == 952) throw new InvalidOperationException("boom from OnRawFrame");
        return false; // let other types (e.g. echo 900) dispatch normally
    }
}

/// <summary>Server that creates <see cref="ThrowingRawPeer"/>s.</summary>
public class ThrowingRawServer : BaseServer
{
    /// <summary>Creates the server.</summary>
    public ThrowingRawServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new ThrowingRawPeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

/// <summary>Client that sends a type-950 frame the server is expected to relay back as 951.</summary>
public class RelayTestClient : BaseClient
{
    /// <summary>Creates the relay-test client.</summary>
    public RelayTestClient(Configuration config) : base(config) { }

    /// <summary>Sends an <see cref="EchoMessage"/> as the relayed type 950.</summary>
    public Task SendRelayInputAsync(string text)
        => SendAsync((ushort)950, new EchoMessage { Text = text }, DeliveryMethod.Reliable);

    /// <inheritdoc/>
    protected override void OnConnected() { }

    /// <inheritdoc/>
    protected override void OnDisconnected() { }

    /// <inheritdoc/>
    protected override void OnError(string error) { }
}

/// <summary>
/// End-to-end test of the raw-frame escape hatch: a relay peer intercepts a frame, forwards the raw bytes
/// with <c>SendRawAsync</c> (no re-serialization), consumes it (skipping typed dispatch), and the forwarded
/// bytes round-trip intact to the client.
/// </summary>
[Collection("integration")]
public class RawFrameTests
{
    [Fact]
    public async Task OnRawFrame_Consumes_And_SendRawAsync_Forwards_Intact()
    {
        RelayProbe.Reset();
        var config = new Configuration { Host = "127.0.0.1", Port = 5861, TransportType = TransportType.Tcp };
        var server = new RelayTestServer(config);
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new RelayTestClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = 5861,
            TransportType = TransportType.Tcp
        });
        await client.ConnectAsync();
        await client.SendRelayInputAsync("relayed");

        // The client gets the message back through the raw-forward path (deserialized by the 951 handler).
        Assert.True(await WaitUntil(() => RelayProbe.ClientReceived.Contains("relayed")));

        // The typed 950 handler was bypassed because OnRawFrame consumed the frame.
        Assert.Equal(0, RelayProbe.ServerTypedRan);

        // The intercepted bytes are exactly what the client serialized — relay never deserialized them.
        var expected = SetNetSerializer.Serialize(new EchoMessage { Text = "relayed" });
        Assert.Equal(expected, RelayProbe.InterceptedRaw);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Throwing_OnRawFrame_Is_Isolated_And_Connection_Survives()
    {
        TestInbox.Reset();
        var config = new Configuration { Host = "127.0.0.1", Port = 5862, TransportType = TransportType.Tcp };
        var server = new ThrowingRawServer(config);
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = 5862,
            TransportType = TransportType.Tcp
        });
        await client.ConnectAsync();

        // This frame makes the server's OnRawFrame throw — it must be isolated, not tear down the connection.
        await client.SendAsTypeAsync(952, "boom", DeliveryMethod.Reliable);
        // A normal echo afterwards must still round-trip, proving the connection survived the throwing hook.
        await client.SendEchoAsync("alive", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ClientReceived.Contains("alive")));

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
