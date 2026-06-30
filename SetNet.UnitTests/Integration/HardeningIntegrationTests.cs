using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// Load/soak/security integration tests for the production-hardening features: connection limits, per-IP
/// rate limiting, no-leak across reconnects, concurrent clients, back-pressure delivery, and TLS.
/// </summary>
[Collection("integration")]
public class HardeningIntegrationTests
{
    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task MaxConnections_RejectsBeyondLimit()
    {
        var serverConfig = new Configuration { Host = "127.0.0.1", Port = 5831, MaxConnectionsLimit = 2 };
        var server = new TestServer(serverConfig);
        _ = server.StartAsync();
        await Task.Delay(200);

        var clients = new List<TestClient>();
        for (int i = 0; i < 4; i++)
        {
            var c = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5831 });
            await c.ConnectAsync();
            clients.Add(c);
            await Task.Delay(50);
        }

        Assert.True(await WaitUntil(() => server.ActiveConnections == 2));
        Assert.True(await WaitUntil(() => serverConfig.Metrics.ConnectionsRejected >= 1));

        foreach (var c in clients) c.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task ManyConcurrentClients_AllDelivered_AndCleanUp()
    {
        const int n = 25;
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5832 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var clients = new List<TestClient>();
        await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
        {
            var c = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5832 });
            await c.ConnectAsync();
            lock (clients) clients.Add(c);
            await c.SendEchoAsync("c" + i, DeliveryMethod.Reliable);
        }));

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= n),
            $"server received {TestInbox.ServerReceived.Count}/{n}");
        Assert.True(await WaitUntil(() => server.ActiveConnections == n));

        foreach (var c in clients) c.Disconnect();
        Assert.True(await WaitUntil(() => server.ActiveConnections == 0), "connections leaked after disconnect");

        await server.StopAsync();
    }

    [Fact]
    public async Task ReconnectStorm_DoesNotLeakPeers()
    {
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5833 });
        _ = server.StartAsync();
        await Task.Delay(200);

        for (int i = 0; i < 8; i++)
        {
            var c = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5833 });
            await c.ConnectAsync();
            await c.SendEchoAsync("r" + i, DeliveryMethod.Reliable);
            await Task.Delay(40);
            c.Disconnect();
            await Task.Delay(40);
        }

        Assert.True(await WaitUntil(() => server.ActiveConnections == 0), "peers leaked across reconnect storm");
        await server.StopAsync();
    }

    [Fact]
    public async Task PerIpRateLimit_RejectsBurst()
    {
        var serverConfig = new Configuration { Host = "127.0.0.1", Port = 5834, MaxConnectionsPerIpPerSecond = 2 };
        var server = new TestServer(serverConfig);
        _ = server.StartAsync();
        await Task.Delay(200);

        var clients = new List<TestClient>();
        for (int i = 0; i < 6; i++)
        {
            var c = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5834 });
            await c.ConnectAsync(); // TCP connects even if the server then rate-rejects it
            clients.Add(c);
        }

        Assert.True(await WaitUntil(() => serverConfig.Metrics.ConnectionsRejected >= 1),
            $"rejected={serverConfig.Metrics.ConnectionsRejected}");

        foreach (var c in clients) c.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Backpressure_GateEnabled_StillDeliversAll()
    {
        const int n = 15;
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5835, MaxInFlightMessages = 2 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5835, MaxInFlightMessages = 2 });
        await client.ConnectAsync();
        for (int i = 0; i < n; i++)
            await client.SendEchoAsync("b" + i, DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= n),
            $"server received {TestInbox.ServerReceived.Count}/{n}");

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task UdpReliableChannels_BothChannelsDeliver()
    {
        TestInbox.Reset();
        Configuration Cfg() => new()
        {
            Host = "127.0.0.1", Port = 5838, TransportType = TransportType.Udp,
            DefaultDelivery = DeliveryMethod.Reliable, UdpReliableChannels = 2, UdpReliableAckTimeoutMs = 50
        };
        var server = new TestServer(Cfg());
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Cfg());
        await client.ConnectAsync();
        await client.SendEchoAsync("ch0", DeliveryMethod.Reliable, 0);
        await client.SendEchoAsync("ch1", DeliveryMethod.Reliable, 1);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("ch0") && TestInbox.ServerReceived.Contains("ch1")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task SendBatching_DeliversAllMessages()
    {
        const int n = 50;
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5837, SendBatching = true });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5837, SendBatching = true });
        await client.ConnectAsync();
        for (int i = 0; i < n; i++)
            await client.SendEchoAsync("x" + i, DeliveryMethod.Reliable); // accumulates in the batch buffer
        await client.FlushManuallyAsync(); // one write for all 50 frames

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= n),
            $"server received {TestInbox.ServerReceived.Count}/{n}");

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task SendBatching_GracefulDisconnect_FlushesPendingBatch()
    {
        // Regression: closing a batched connection must flush buffered frames instead of dropping them.
        // A long auto-flush interval guarantees the only delivery path is the flush inside Close().
        const int n = 25;
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5841, SendBatching = true, SendBatchFlushMs = 60000 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5841, SendBatching = true, SendBatchFlushMs = 60000 });
        await client.ConnectAsync();
        for (int i = 0; i < n; i++)
            await client.SendEchoAsync("g" + i, DeliveryMethod.Reliable); // buffered; auto-flush won't fire for 60s
        client.Disconnect(); // Close() must flush the buffered batch before the socket is torn down

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= n),
            $"server received {TestInbox.ServerReceived.Count}/{n} — batch was dropped on close");

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_StartsReceive_EvenWhenOnNewClientForgot()
    {
        // Regression (#18): a peer whose OnNewClient does NOT call StartReceive must still receive, because the
        // framework now calls the (idempotent) StartReceive itself. Previously this silently stalled.
        TestInbox.Reset();
        var server = new ForgetfulServer(new Configuration { Host = "127.0.0.1", Port = 5842 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5842 });
        await client.ConnectAsync();
        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Count >= 1),
            "server never received — framework did not auto-start the receive loop");

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task PeerClosedDuringOnNewClient_IsNotRegistered()
    {
        var server = new ClosingDuringOnNewClientServer(new Configuration { Host = "127.0.0.1", Port = 5845 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5845 });
        await client.ConnectAsync();

        Assert.True(await WaitUntil(() => server.ActiveConnections == 0),
            "closed peer was registered in ActiveConnections");

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task ClientLifecycleHookThrow_DoesNotEscapeDisconnect()
    {
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5846 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new ThrowingDisconnectedClient(new Configuration { Host = "127.0.0.1", Port = 5846 });
        await client.ConnectAsync();

        var ex = Record.Exception(() => client.Disconnect());

        Assert.Null(ex);
        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.Equal(1, client.DisconnectedCalls);

        await server.StopAsync();
    }

    [Fact]
    public async Task PeerLifecycleHookThrow_StillRemovesClient()
    {
        var server = new ThrowingPeerServer(new Configuration { Host = "127.0.0.1", Port = 5847 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5847 });
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => server.ActiveConnections == 1));

        client.Disconnect();

        Assert.True(await WaitUntil(() => server.ActiveConnections == 0),
            "throwing peer hook prevented server-pool cleanup");

        await server.StopAsync();
    }

    [Fact]
    public async Task IntentionalDisconnect_FiresOnDisconnectedExactlyOnce()
    {
        // Regression (#11/#5): an explicit Disconnect() — even called twice — must fire OnDisconnected once and
        // never spuriously reconnect.
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5843 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new CountingClient(new Configuration { Host = "127.0.0.1", Port = 5843 });
        await client.ConnectAsync();
        await Task.Delay(100);

        client.Disconnect();
        client.Disconnect(); // second call must be a no-op
        await Task.Delay(200);

        Assert.Equal(1, client.DisconnectedCount);
        await server.StopAsync();
    }

    [Fact]
    public async Task GatedClient_AfterReconnectCycle_StillReceives()
    {
        // Regression (#14 fix follow-up): with MaxInFlightMessages>0 the dispatch gate's cancellation source was
        // cancelled on the first teardown and never re-armed, so a reconnected client silently died on the first
        // received frame. A Disconnect→Connect cycle (same code path as auto-reconnect's ResetDispatch) must
        // still deliver received messages.
        TestInbox.Reset();
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = 5844, MaxInFlightMessages = 2 });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = 5844, MaxInFlightMessages = 2 });
        await client.ConnectAsync();
        await client.SendEchoAsync("first", DeliveryMethod.Reliable);
        Assert.True(await WaitUntil(() => TestInbox.ClientReceived.Contains("first")),
            "gated client did not receive the echo on the first connection");

        // Reconnect cycle: Disconnect() cancels the dispatch gate's CTS; the next connect must re-arm it.
        client.Disconnect();
        await Task.Delay(100);
        await client.ConnectAsync();
        await client.SendEchoAsync("second", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ClientReceived.Contains("second")),
            "reconnected gated client never received the echo — the dispatch gate CTS was not re-armed");

        client.Disconnect();
        await server.StopAsync();
    }

    [SkippableFact]
    public async Task Tls_Echo_RoundTrips()
    {
        TestInbox.Reset();

        // Self-signed cert generation can fail on some hosts (e.g. macOS keychain interop); skip there.
        X509Certificate2? generated = null;
        try { generated = CreateSelfSignedCertificate(); }
        catch (Exception ex) { Skip.If(true, "Self-signed certificate generation unavailable on this host: " + ex.Message); }
        using var cert = generated!;

        var server = new TestServer(new Configuration
        {
            Host = "127.0.0.1", Port = 5836,
            UseSsl = true, ServerCertificate = cert
        });
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(new Configuration
        {
            Host = "127.0.0.1", Port = 5836,
            UseSsl = true,
            ServerCertificateValidationCallback = (_, _, _, _) => true // accept the self-signed cert
        });
        await client.ConnectAsync();
        await client.SendEchoAsync("secret", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() =>
            TestInbox.ServerReceived.Contains("secret") && TestInbox.ClientReceived.Contains("secret")));

        client.Disconnect();
        await server.StopAsync();
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PKCS#12 so SslStream has a persisted, usable private key on all platforms.
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    private sealed class ClosingDuringOnNewClientServer : BaseServer
    {
        public ClosingDuringOnNewClientServer(Configuration config) : base(config) { }

        protected override BasePeer OnNewClient(PeerInfo peerInfo)
        {
            var peer = new ClosingPeer(peerInfo);
            peer.Close();
            return peer;
        }
    }

    private sealed class ClosingPeer : BasePeer
    {
        public ClosingPeer(PeerInfo currentPeerInfo) : base(currentPeerInfo) { }

        protected override void OnDisconnected() { }
    }

    private sealed class ThrowingDisconnectedClient : BaseClient
    {
        public int DisconnectedCalls;

        public ThrowingDisconnectedClient(Configuration config) : base(config) { }

        protected override void OnConnected() { }

        protected override void OnDisconnected()
        {
            DisconnectedCalls++;
            throw new InvalidOperationException("client hook boom");
        }

        protected override void OnError(string error) { }
    }

    private sealed class ThrowingPeerServer : BaseServer
    {
        public ThrowingPeerServer(Configuration config) : base(config) { }

        protected override BasePeer OnNewClient(PeerInfo peerInfo) => new ThrowingPeer(peerInfo);
    }

    private sealed class ThrowingPeer : BasePeer
    {
        public ThrowingPeer(PeerInfo currentPeerInfo) : base(currentPeerInfo) { }

        protected override void OnDisconnected()
        {
            throw new InvalidOperationException("peer hook boom");
        }
    }
}
