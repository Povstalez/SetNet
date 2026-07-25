using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Client that records every lifecycle callback so tests can assert how a disconnect was classified.</summary>
public class LifecycleClient : BaseClient
{
    /// <summary>Number of reconnect attempts announced via <see cref="OnReconnecting"/>.</summary>
    public int Reconnecting;

    /// <summary>Number of times the loss was classified as unexpected.</summary>
    public int Unexpected;

    /// <summary>Number of terminal disconnects.</summary>
    public int Disconnected;

    /// <summary>Number of successful reconnects.</summary>
    public int Reconnected;

    /// <summary>The reason from the server's kick notice, if one arrived.</summary>
    public volatile string? KickReason;

    /// <summary>Number of deliberate kicks announced by the server.</summary>
    public int Kicks;

    /// <summary>Creates the client.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public LifecycleClient(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override void OnConnected() { }

    /// <inheritdoc/>
    protected override void OnDisconnected() => Interlocked.Increment(ref Disconnected);

    /// <inheritdoc/>
    protected override void OnError(string error) { }

    /// <inheritdoc/>
    protected override void OnUnexpectedDisconnect() => Interlocked.Increment(ref Unexpected);

    /// <inheritdoc/>
    protected override void OnReconnecting(int attempt, int maxAttempts) => Interlocked.Increment(ref Reconnecting);

    /// <inheritdoc/>
    protected override void OnReconnected() => Interlocked.Increment(ref Reconnected);

    /// <inheritdoc/>
    protected override void OnKicked(string? reason)
    {
        KickReason = reason;
        Interlocked.Increment(ref Kicks);
    }

    /// <summary>Sends a raw application frame, ignoring failures on a dying link.</summary>
    /// <param name="type">Wire type id.</param>
    public async Task PokeAsync(ushort type)
    {
        try { await SendRawAsync(type, new byte[] { 1 }); } catch { /* link down */ }
    }
}

/// <summary>Server that keeps a handle on the last accepted peer so tests can close it from the server side.</summary>
public class LifecycleServer : BaseServer
{
    /// <summary>The most recently accepted peer.</summary>
    public volatile BasePeer? LastPeer;

    /// <summary>Creates the server.</summary>
    /// <param name="config">Transport/endpoint settings.</param>
    public LifecycleServer(Configuration config) : base(config) { }

    /// <inheritdoc/>
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new TestPeer(peerInfo);
        LastPeer = peer;
        peer.StartReceive();
        return peer;
    }
}

/// <summary>
/// Regression tests for connection-loss classification and auto-reconnect: a drop that the application did not ask
/// for must reach <c>OnUnexpectedDisconnect</c> and reconnect, on every transport — including UDP/Both, whose
/// receive path signals every loss as an orderly end-of-stream rather than an exception.
/// </summary>
[Collection("Integration")]
public class ReconnectTests
{
    private static Configuration Config(TransportType transport, int port, bool autoReconnect = true) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        UdpPort = port,
        TransportType = transport,
        UdpReliabilityEnabled = true,
        DefaultDelivery = DeliveryMethod.Reliable,
        AutoReconnect = autoReconnect,
        MaxReconnectAttempts = 3,
        ReconnectDelayMs = 100,
        HeartbeatEnabled = false, // isolate the close path from the heartbeat detector
    };

    /// <summary>A server-side close is a loss the client did not ask for, so it must reconnect on every transport.</summary>
    [Theory]
    [InlineData(TransportType.Tcp, 5871)]
    [InlineData(TransportType.Udp, 5872)]
    [InlineData(TransportType.Both, 5873)]
    public async Task RemoteClose_ReconnectsOnEveryTransport(TransportType transport, int port)
    {
        var server = new LifecycleServer(Config(transport, port));
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(Config(transport, port));
        await client.ConnectAsync();
        await Task.Delay(300);

        var first = server.LastPeer;
        Assert.NotNull(first);
        first!.Close(); // server restart / kick / idle-expiry all look like this to the client

        await WaitUntil(() => client.Reconnected > 0, 3000);

        Assert.True(client.Unexpected >= 1, "a remote close must be reported as an unexpected loss");
        Assert.True(client.Reconnecting >= 1, "auto-reconnect must be attempted");
        Assert.Equal(1, client.Reconnected);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Equal(0, client.Disconnected); // reconnect succeeded, so no terminal disconnect

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>Opting out restores the old behaviour: a remote close is terminal and nothing reconnects.</summary>
    [Fact]
    public async Task RemoteClose_IsTerminal_WhenReconnectOnRemoteCloseDisabled()
    {
        var config = Config(TransportType.Tcp, 5874);
        config.ReconnectOnRemoteClose = false;

        var server = new LifecycleServer(Config(TransportType.Tcp, 5874));
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(config);
        await client.ConnectAsync();
        await Task.Delay(300);

        server.LastPeer!.Close();
        await WaitUntil(() => client.Disconnected > 0, 2000);

        Assert.Equal(0, client.Reconnecting);
        Assert.Equal(0, client.Unexpected);
        Assert.Equal(1, client.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, client.State);

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>A close the application asked for stays terminal — it must never trigger a reconnect.</summary>
    [Fact]
    public async Task IntentionalDisconnect_DoesNotReconnect()
    {
        var server = new LifecycleServer(Config(TransportType.Tcp, 5875));
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(Config(TransportType.Tcp, 5875));
        await client.ConnectAsync();
        await Task.Delay(200);

        client.Disconnect();
        await Task.Delay(800);

        Assert.Equal(0, client.Reconnecting);
        Assert.Equal(0, client.Unexpected);
        Assert.Equal(1, client.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, client.State);

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>
    /// Reconnecting the same client object by hand: the previous connection's receive loop is still unwinding while
    /// the new one is established, and must not close it, cancel its dispatch gate, or fire a terminal disconnect.
    /// </summary>
    [Fact]
    public async Task DisconnectThenConnect_LeavesTheNewConnectionAlive()
    {
        var server = new LifecycleServer(Config(TransportType.Tcp, 5876, autoReconnect: false));
        _ = server.StartAsync();
        await Task.Delay(300);

        for (var i = 0; i < 40; i++)
        {
            var client = new LifecycleClient(Config(TransportType.Tcp, 5876, autoReconnect: false));
            await client.ConnectAsync();
            client.Disconnect();
            await client.ConnectAsync(); // races the previous loop's teardown
            await Task.Delay(60);

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.Equal(1, client.Disconnected); // exactly the one we asked for
            await client.PokeAsync(4321);         // the live connection must still be usable
            Assert.Equal(ConnectionState.Connected, client.State);

            client.Dispose();
        }

        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>
    /// A client that sends application traffic but no pings is alive: the server's heartbeat watcher must key off
    /// inbound traffic, not off Ping frames alone, or a heartbeat-configuration mismatch silently kills every peer.
    /// </summary>
    [Fact]
    public async Task ServerHeartbeat_KeepsClientThatSendsTrafficButNoPings()
    {
        var serverConfig = Config(TransportType.Tcp, 5877, autoReconnect: false);
        serverConfig.HeartbeatEnabled = true;
        serverConfig.HeartbeatIntervalMs = 100;
        serverConfig.HeartbeatTimeoutMs = 400;

        var server = new LifecycleServer(serverConfig);
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(Config(TransportType.Tcp, 5877, autoReconnect: false)); // heartbeat off
        await client.ConnectAsync();

        for (var i = 0; i < 15; i++) // ~1.5s, well past the 400ms timeout
        {
            await client.PokeAsync(4321);
            await Task.Delay(100);
        }

        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Equal(1, server.ActiveConnections);
        Assert.Equal(0, client.Disconnected);

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>A silent client with no traffic at all is still reaped once the heartbeat window elapses.</summary>
    [Fact]
    public async Task ServerHeartbeat_StillReapsASilentClient()
    {
        var serverConfig = Config(TransportType.Tcp, 5878, autoReconnect: false);
        serverConfig.HeartbeatEnabled = true;
        serverConfig.HeartbeatIntervalMs = 100;
        serverConfig.HeartbeatTimeoutMs = 300;

        var server = new LifecycleServer(serverConfig);
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(Config(TransportType.Tcp, 5878, autoReconnect: false)); // never pings
        await client.ConnectAsync();

        await WaitUntil(() => server.ActiveConnections == 0, 3000);
        Assert.Equal(0, server.ActiveConnections);

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>
    /// A deliberate kick (ban, geo-block, session displaced by a newer login) must be terminal even with
    /// auto-reconnect on — otherwise the client dials straight back into the same kick, and two clients sharing an
    /// account under KickExisting kick each other forever.
    /// </summary>
    [Fact]
    public async Task Kick_IsTerminal_EvenWithAutoReconnect()
    {
        var server = new LifecycleServer(Config(TransportType.Tcp, 5879));
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new LifecycleClient(Config(TransportType.Tcp, 5879));
        await client.ConnectAsync();
        await Task.Delay(300);

        server.LastPeer!.CurrentPeerInfo.Kick("banned");
        await WaitUntil(() => client.Disconnected > 0, 3000);

        Assert.Equal("banned", client.KickReason);
        Assert.Equal(0, client.Reconnecting);
        Assert.Equal(0, client.Unexpected);
        Assert.Equal(1, client.Disconnected);
        Assert.Equal(ConnectionState.Disconnected, client.State);

        // And it stays down: no late reconnect creeps in after the callbacks.
        await Task.Delay(700);
        Assert.Equal(0, client.Reconnecting);
        Assert.Equal(0, server.ActiveConnections);

        client.Dispose();
        await server.StopAsync();
        server.Dispose();
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or the budget runs out.</summary>
    private static async Task WaitUntil(System.Func<bool> condition, int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs && !condition())
        {
            await Task.Delay(25);
            waited += 25;
        }
    }
}
