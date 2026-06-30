using System.Diagnostics;
using MessagePack;
using SetNet.Config;
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Tests.Data;

namespace SetNet.Tests;

/// <summary>Tiny benchmark message.</summary>
[MessagePackObject]
public class BenchMessage
{
    [Key(0)] public int N { get; set; }
}

/// <summary>Process-wide sink counter for the throughput benchmark.</summary>
public static class BenchStats
{
    private static long _received;
    public static long Received => System.Threading.Interlocked.Read(ref _received);
    public static void Increment() => System.Threading.Interlocked.Increment(ref _received);
    public static void Reset() => System.Threading.Interlocked.Exchange(ref _received, 0);
}

/// <summary>Server-side pure sink handler (no echo) used to measure ingest throughput.</summary>
[MessageHandler(800)]
public class BenchSinkHandler : IServerMessageHandler
{
    public System.Threading.Tasks.Task HandleAsync(BasePeer peer, byte[] data)
    {
        BenchStats.Increment();
        return System.Threading.Tasks.Task.CompletedTask;
    }
}

public class BenchPeer : BasePeer
{
    public BenchPeer(PeerInfo info) : base(info) { }
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
}

public class BenchServer : BaseServer
{
    public BenchServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new BenchPeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

public class BenchClient : BaseClient
{
    public BenchClient(Configuration config) : base(config) { }
    public Task SendBenchAsync(int n) => SendAsync((ushort)800, new BenchMessage { N = n });
    public Task FlushBenchAsync() => FlushAsync();
    protected override void OnConnected() { }
    protected override void OnDisconnected() { }
    protected override void OnError(string error) { }
}

public static class Bench
{
    public static async Task RunAsync()
    {
        Console.WriteLine($"=== SetNet benchmark (GC server={System.Runtime.GCSettings.IsServerGC}, cores={Environment.ProcessorCount}) ===");

        // ── 1. Single-connection message throughput (tiny reliable TCP messages) ──
        // Measured twice: the default latency-first path (TcpNoDelay=true, each message sent immediately) and
        // the throughput path (SendBatching=true, coalesced into one write per flush). NoDelay is left on for
        // both — batching gives high throughput AND low latency, which is the recommended high-rate config.
        const int messages = 100_000;

        async Task RunThroughput(bool batching, string label, int port)
        {
            BenchStats.Reset();
            var server = new BenchServer(new Configuration { Host = "127.0.0.1", Port = port, SendBatching = batching, Logger = new SetNet.Logging.NoOpLogger() });
            _ = server.StartAsync();
            await Task.Delay(300);
            var client = new BenchClient(new Configuration { Host = "127.0.0.1", Port = port, SendBatching = batching, Logger = new SetNet.Logging.NoOpLogger() });
            await client.ConnectAsync();

            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < messages; i++)
                await client.SendBenchAsync(i);
            await client.FlushBenchAsync(); // flush the final batch (no-op when batching is off)
            while (BenchStats.Received < messages) await Task.Delay(2);
            sw2.Stop();

            var r = messages / sw2.Elapsed.TotalSeconds;
            Console.WriteLine($"[throughput:{label}] {messages:N0} msgs in {sw2.Elapsed.TotalMilliseconds:N0} ms = {r:N0} msgs/sec on ONE connection");
            client.Disconnect();
            await server.StopAsync();
            await Task.Delay(200);
        }

        await RunThroughput(batching: false, label: "default", port: 5710);
        await RunThroughput(batching: true, label: "batched", port: 5712);

        // ── 2. Many idle connections: memory + connect time ──
        const int conns = 2000;
        var connServer = new BenchServer(new Configuration { Host = "127.0.0.1", Port = 5711, MaxConnectionsLimit = conns + 100, Logger = new SetNet.Logging.NoOpLogger() });
        _ = connServer.StartAsync();
        await Task.Delay(300);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var memBefore = Process.GetCurrentProcess().WorkingSet64;

        var clients = new List<BenchClient>(conns);
        var sw2 = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, conns).Select(async _ =>
        {
            var c = new BenchClient(new Configuration { Host = "127.0.0.1", Port = 5711, Logger = new SetNet.Logging.NoOpLogger() });
            await c.ConnectAsync();
            lock (clients) clients.Add(c);
        }));
        sw2.Stop();
        await Task.Delay(500); // let server finish accepting

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var memAfter = Process.GetCurrentProcess().WorkingSet64;
        var deltaMb = (memAfter - memBefore) / 1024.0 / 1024.0;
        // Both endpoints (client + server peer) live in THIS process, so divide by 2*conns for per-endpoint.
        var perEndpointKb = (memAfter - memBefore) / 1024.0 / (2.0 * conns);

        Console.WriteLine($"[connections] established {connServer.ActiveConnections:N0} server peers in {sw2.Elapsed.TotalMilliseconds:N0} ms");
        Console.WriteLine($"[connections] working-set delta {deltaMb:N0} MB for {conns:N0} client+{conns:N0} peer endpoints");
        Console.WriteLine($"[connections] ~{perEndpointKb:N0} KB per endpoint (in-process, both ends)");

        foreach (var c in clients) c.Disconnect();
        await connServer.StopAsync();
        Console.WriteLine("=== benchmark done ===");
    }
}
