using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// Regression tests for connect deadlines. A server that accepts the TCP connection and then goes silent must fail
/// the attempt within the configured budget: the handshakes that follow the accept have no deadline of their own,
/// and a socket read already in flight is not reliably interrupted by a cancellation token, so without an explicit
/// bound <c>ConnectAsync</c> — and every auto-reconnect attempt behind it — would hang forever with no callback.
/// </summary>
[Collection("Integration")]
public class ConnectTimeoutTests
{
    private const int TimeoutMs = 1200;

    /// <summary>A client that never sends anything; only the connect path is under test.</summary>
    private sealed class SilentClient : BaseClient
    {
        public SilentClient(Configuration config) : base(config) { }
        protected override void OnConnected() { }
        protected override void OnDisconnected() { }
        protected override void OnError(string error) { }
    }

    /// <summary>Accepts TCP connections and then says nothing at all, holding them open.</summary>
    private sealed class SilentListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly System.Collections.Generic.List<TcpClient> _accepted = new();

        public SilentListener(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_accepted) _accepted.Add(client); // hold it open, send nothing
                }
            }
            catch { /* listener stopped */ }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            lock (_accepted)
                foreach (var c in _accepted) { try { c.Close(); } catch { } }
        }
    }

    /// <summary>The TLS handshake must be bounded by <see cref="Configuration.ConnectTimeoutMs"/>.</summary>
    [Fact]
    public async Task TlsHandshake_AgainstASilentServer_TimesOut()
    {
        using var silent = new SilentListener(5881);

        var client = new SilentClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = 5881,
            UseSsl = true,
            ConnectTimeoutMs = TimeoutMs,
            ServerCertificateValidationCallback = (_, _, _, _) => true,
            HeartbeatEnabled = false,
        });

        await AssertFailsWithinBudget(client);
        client.Dispose();
    }

    /// <summary>The Both-mode wait for the server's UDP bind token must be bounded the same way.</summary>
    [Fact]
    public async Task BothMode_BindTokenWait_AgainstASilentServer_TimesOut()
    {
        using var silent = new SilentListener(5882);

        var client = new SilentClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = 5882,
            UdpPort = 5882,
            TransportType = TransportType.Both,
            ConnectTimeoutMs = TimeoutMs,
            HeartbeatEnabled = false,
        });

        await AssertFailsWithinBudget(client);
        client.Dispose();
    }

    /// <summary>
    /// Asserts the connect fails (rather than hanging) well inside a generous budget, and leaves the client back in
    /// Disconnected so the application can retry.
    /// </summary>
    private static async Task AssertFailsWithinBudget(BaseClient client)
    {
        var connect = Task.Run(async () =>
        {
            try { await client.ConnectAsync(); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        });

        var finished = await Task.WhenAny(connect, Task.Delay(TimeoutMs * 5));
        Assert.True(finished == connect, "ConnectAsync hung past the connect timeout instead of failing");

        var error = await connect;
        Assert.NotNull(error); // a silent server must never look like a successful connect
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}
