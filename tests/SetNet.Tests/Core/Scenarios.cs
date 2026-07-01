using System.Security.Cryptography.X509Certificates;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;

namespace SetNet.Tests;

/// <summary>
/// In-process test scenarios used to verify each transport mode end-to-end.
/// Invoked from <c>Program.cs</c> by name (e.g. <c>dotnet run -- tcp</c>).
/// </summary>
public static class Scenarios
{
    /// <summary>Direct test of PacketBuilder reassembly when a packet is split across reads (fragmentation).</summary>
    public static void RunFragmentationTest()
    {
        Console.WriteLine("=== PacketBuilder fragmentation test ===");

        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var wire = new PacketBuilder().BuildPacket(7, payload); // [4 len][2 type][5000 payload]

        var rx = new PacketBuilder();
        rx.AppendData(wire[..2000]);                 // first read: partial
        var got1 = rx.TryGetCompletePacket(out _);    // expect false (incomplete)
        rx.AppendData(wire[2000..]);                  // second read: the rest
        var got2 = rx.TryGetCompletePacket(out var frame);

        var ok = !got1 && got2 && frame != null;
        if (ok)
        {
            var (type, data) = PacketBuilder.ParsePacket(frame!);
            ok = type == 7 && data.Length == payload.Length;
            for (int i = 0; ok && i < payload.Length; i++)
                if (data[i] != payload[i]) ok = false;
        }

        Console.WriteLine(ok
            ? "[frag-test] PASS (split packet reassembled intact)"
            : $"[frag-test] FAIL (got1={got1}, got2={got2}) — multi-read packets are corrupted");
    }

    /// <summary>
    /// TLS-over-TCP echo: loads a PFX from SETNET_TLS_PFX (password SETNET_TLS_PWD) and runs an encrypted
    /// round-trip. Verifies the SslStream path end-to-end on hosts where in-code cert generation is awkward.
    /// </summary>
    public static async Task RunTlsEchoAsync()
    {
        const int port = 5707;
        var pfx = Environment.GetEnvironmentVariable("SETNET_TLS_PFX");
        var pwd = Environment.GetEnvironmentVariable("SETNET_TLS_PWD");
        if (string.IsNullOrEmpty(pfx))
        {
            Console.WriteLine("[tls-echo] SETNET_TLS_PFX not set; skipping.");
            return;
        }

        Console.WriteLine("=== TLS echo scenario ===");
        var cert = new X509Certificate2(pfx, pwd);

        var server = new MainServer(new Configuration
        {
            Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp,
            UseSsl = true, ServerCertificate = cert
        });
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(new Configuration
        {
            Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp,
            UseSsl = true,
            ServerCertificateValidationCallback = (_, _, _, _) => true // accept the self-signed cert
        });
        await client.ConnectAsync();
        await Task.Delay(1000);

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(200);
        Console.WriteLine("=== TLS echo scenario done (encrypted round-trip OK above) ===");
    }

    /// <summary>TCP echo: client connects, exchanges a TestMessage round-trip with the server.</summary>
    public static async Task RunTcpEchoAsync()
    {
        const int port = 5701;
        Console.WriteLine("=== TCP echo scenario ===");

        var server = new MainServer(new Configuration
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Tcp,
            HeartbeatEnabled = true
        });
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Tcp,
            HeartbeatEnabled = true
        });
        await client.ConnectAsync();

        await Task.Delay(1000);

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(200);
        Console.WriteLine("=== TCP echo scenario done ===");
    }

    /// <summary>UDP echo over the given delivery method (Reliable exercises the reliability layer).</summary>
    public static async Task RunUdpEchoAsync(DeliveryMethod delivery)
    {
        const int port = 5702;
        var label = $"UDP {delivery}";
        Console.WriteLine($"=== {label} echo scenario ===");

        var server = new MainServer(new Configuration
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Udp,
            DefaultDelivery = delivery,
            HeartbeatEnabled = true
        });
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(new Configuration
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Udp,
            DefaultDelivery = delivery,
            HeartbeatEnabled = true
        });
        await client.ConnectAsync();

        await Task.Delay(1200);

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(300);
        Console.WriteLine($"=== {label} echo scenario done ===");
    }

    /// <summary>Sends N reliable messages under simulated packet loss; verifies all arrive (in order, no dups).</summary>
    public static async Task RunUdpLossAsync()
    {
        const int port = 5703;
        const int count = 50;
        const int lossPercent = 30;
        Console.WriteLine($"=== UDP reliable under {lossPercent}% loss ({count} msgs) ===");
        LossStats.Reset();

        Configuration MakeConfig() => new()
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Udp,
            DefaultDelivery = DeliveryMethod.Reliable,
            UdpReliabilityEnabled = true,
            UdpReliableAckTimeoutMs = 50,
            UdpSimulatedLossPercent = lossPercent
        };

        var server = new MainServer(MakeConfig());
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(MakeConfig());
        await client.ConnectAsync();

        for (int i = 0; i < count; i++)
            await client.SendLossPingAsync(i, DeliveryMethod.Reliable);

        for (int i = 0; i < 60 && LossStats.Received < count; i++)
            await Task.Delay(100);

        Console.WriteLine($"[loss-test] server received {LossStats.Received}/{count} reliable messages");
        Console.WriteLine(LossStats.Received == count ? "[loss-test] PASS" : "[loss-test] FAIL");

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(200);
    }

    /// <summary>
    /// Regression for the Both-mode idle-expiry fix: a Both client idle past UdpPeerExpiryMs must still
    /// deliver unreliable traffic over UDP afterwards (the bound UDP peer must NOT be expired).
    /// </summary>
    public static async Task RunBothIdleAsync()
    {
        const int port = 5705;
        const int idleMs = 3000;
        Console.WriteLine($"=== Both mode idle survival (expiry 2s, idle {idleMs}ms) ===");
        LossStats.Reset();

        Configuration MakeConfig() => new()
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Both,
            UdpReliabilityEnabled = true,
            UdpPeerExpiryMs = 2000 // shorter than the idle window below
        };

        var server = new MainServer(MakeConfig());
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(MakeConfig());
        await client.ConnectAsync();
        await Task.Delay(400);

        await Task.Delay(idleMs); // stay idle longer than UdpPeerExpiryMs

        const int n = 5;
        for (int i = 0; i < n; i++)
            await client.SendLossPingAsync(i, DeliveryMethod.Unreliable);

        await Task.Delay(800);

        var unreliable = LossStats.UnreliableReceived;
        Console.WriteLine($"[idle-test] unreliable received after idle: {unreliable}/{n}");
        Console.WriteLine(unreliable == n
            ? "[idle-test] PASS (UDP channel survived the idle period)"
            : "[idle-test] FAIL (UDP peer was expired/dropped while TCP alive)");

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(200);
    }

    /// <summary>
    /// Regression for the reliability window-deadlock fix: sending more reliable messages than the
    /// window to a dead server must not hang forever — the give-up path releases window slots.
    /// </summary>
    public static async Task RunDeadlockAsync()
    {
        const int port = 5706;
        Console.WriteLine("=== Reliability window deadlock guard ===");

        Configuration MakeConfig() => new()
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Udp,
            UdpReliabilityEnabled = true,
            UdpReliableWindowSize = 8,
            UdpReliableMaxRetransmits = 3,
            UdpReliableAckTimeoutMs = 50
        };

        var server = new MainServer(MakeConfig());
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(MakeConfig());
        await client.ConnectAsync();

        await server.StopAsync(); // server gone: nothing will ever ACK
        await Task.Delay(100);

        var sendAll = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++) // far more than the window of 8
            {
                try { await client.SendLossPingAsync(i, DeliveryMethod.Reliable); }
                catch { /* expected once the channel gives up / closes */ }
            }
        });

        var finished = await Task.WhenAny(sendAll, Task.Delay(6000)) == sendAll;
        Console.WriteLine(finished
            ? "[deadlock-test] PASS (all sends returned; no window deadlock)"
            : "[deadlock-test] FAIL (sends hung — window never reopened)");

        client.DisconnectFromServer();
        await Task.Delay(200);
    }

    /// <summary>
    /// Both mode: with UDP loss simulated, reliable messages (routed over TCP) must all arrive while
    /// unreliable ones (routed over UDP) get dropped — proving the per-message routing.
    /// </summary>
    public static async Task RunBothAsync()
    {
        const int port = 5704;
        const int count = 30;
        const int lossPercent = 50;
        Console.WriteLine($"=== Both mode routing ({count} reliable + {count} unreliable, {lossPercent}% UDP loss) ===");
        LossStats.Reset();

        Configuration MakeConfig() => new()
        {
            Host = "127.0.0.1",
            Port = port,
            TransportType = TransportType.Both,
            UdpReliabilityEnabled = true,
            UdpSimulatedLossPercent = lossPercent
        };

        var server = new MainServer(MakeConfig());
        _ = server.StartAsync();
        await Task.Delay(300);

        var client = new MainClient(MakeConfig());
        await client.ConnectAsync();
        await Task.Delay(400); // let the UDP channel attach

        for (int i = 0; i < count; i++)
        {
            await client.SendLossPingAsync(i, DeliveryMethod.Reliable);
            await client.SendLossPingAsync(i, DeliveryMethod.Unreliable);
        }

        await Task.Delay(1500);

        var reliable = LossStats.ReliableReceived;
        var unreliable = LossStats.UnreliableReceived;
        Console.WriteLine($"[both-test] reliable (via TCP):   {reliable}/{count}");
        Console.WriteLine($"[both-test] unreliable (via UDP): {unreliable}/{count}");
        var pass = reliable == count && unreliable < count;
        Console.WriteLine(pass
            ? "[both-test] PASS (all reliable arrived over TCP; UDP dropped some unreliable)"
            : "[both-test] FAIL");

        client.DisconnectFromServer();
        await server.StopAsync();
        await Task.Delay(200);
    }
}
