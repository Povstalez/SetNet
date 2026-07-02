using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Streams;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for large-payload streaming: chunked upload with progress, server → client push,
/// rejection, and resume after a cancelled attempt.
/// </summary>
[Collection("integration")]
public class StreamsTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        new Random(1234).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Upload_With_Progress_Delivers_Exact_Bytes()
    {
        var server = new TestServer(Config("streams-up"));
        var hub = server.UseStreams(new StreamsOptions { ChunkSize = 4 * 1024 });
        var received = new ConcurrentQueue<CompletedStream>();
        hub.Received += (_, s) => received.Enqueue(s);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("streams-up"));
        var streams = client.UseStreams(new StreamsOptions { ChunkSize = 4 * 1024 });
        await client.ConnectAsync();

        var payload = RandomBytes(100_000);   // 100 KB over 4 KB chunks = 25 data frames
        double maxProgress = 0;
        // Progress<T> posts callbacks without ordering guarantees — track the max, not the last.
        var progress = new Progress<double>(p => { lock (payload) maxProgress = Math.Max(maxProgress, p); });

        await streams.SendAsync("blob.bin", payload, progress);

        Assert.True(await WaitUntil(() => !received.IsEmpty));
        Assert.True(received.TryDequeue(out var completed));
        Assert.Equal("blob.bin", completed!.Name);
        Assert.Equal(payload.Length, completed.Length);
        Assert.Equal(payload, ((MemoryStreamSink)completed.Sink).ToArray());
        Assert.True(await WaitUntil(() => { lock (payload) return maxProgress >= 1; }));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Server_Pushes_Stream_To_Client()
    {
        var server = new TestServer(Config("streams-down"));
        var hub = server.UseStreams(new StreamsOptions { ChunkSize = 8 * 1024 });
        var peers = new ConcurrentQueue<SetNet.Core.BasePeer>();
        server.PeerConnected += p => peers.Enqueue(p);   // subscribe before any client connects
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("streams-down"));
        var streams = client.UseStreams();
        var received = new ConcurrentQueue<CompletedStream>();
        streams.Received += s => received.Enqueue(s);
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => !peers.IsEmpty));

        var payload = RandomBytes(50_000);
        Assert.True(peers.TryDequeue(out var peer));
        await hub.SendAsync(peer!, "map.pak", payload);

        Assert.True(await WaitUntil(() => !received.IsEmpty));
        Assert.True(received.TryDequeue(out var completed));
        Assert.Equal("map.pak", completed!.Name);
        Assert.Equal(payload, ((MemoryStreamSink)completed.Sink).ToArray());

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Rejected_Offer_Faults_The_Send()
    {
        var server = new TestServer(Config("streams-reject"));
        var hub = server.UseStreams();
        hub.OfferReceived += (peer, offer) => { _ = offer.RejectAsync("No uploads today."); };
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("streams-reject"));
        var streams = client.UseStreams();
        await client.ConnectAsync();

        var ex = await Assert.ThrowsAsync<StreamsException>(() => streams.SendAsync("nope.bin", RandomBytes(1000)));
        Assert.Contains("No uploads today", ex.Message);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Cancelled_Upload_Resumes_From_Partial()
    {
        var server = new TestServer(Config("streams-resume"));
        var hub = server.UseStreams(new StreamsOptions { ChunkSize = 1024 });
        var received = new ConcurrentQueue<CompletedStream>();
        hub.Received += (_, s) => received.Enqueue(s);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("streams-resume"));
        var streams = client.UseStreams(new StreamsOptions { ChunkSize = 1024 });
        await client.ConnectAsync();

        var payload = RandomBytes(64 * 1024);   // 64 chunks of 1 KB

        // First attempt: a content stream that cancels the token after 16 chunk reads — deterministic mid-abort.
        using var cts = new CancellationTokenSource();
        var abortingContent = new CancelAfterReadsStream(payload, reads: 16, cts);

        var streamId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<StreamsException>(
            () => streams.SendAsync("save.dat", abortingContent, null, streamId, cts.Token));
        Assert.Equal(streamId, ex.StreamId);
        Assert.True(received.IsEmpty);   // nothing completed yet

        // Second attempt with the same id resumes from the receiver's partial and completes with intact bytes.
        await streams.SendAsync("save.dat", new MemoryStream(payload), streamId: streamId);

        Assert.True(await WaitUntil(() => !received.IsEmpty));
        Assert.True(received.TryDequeue(out var completed));
        Assert.Equal(payload, ((MemoryStreamSink)completed!.Sink).ToArray());

        client.Disconnect();
        await server.StopAsync();
    }

    /// <summary>A seekable content stream that cancels a token after a fixed number of reads (to abort an upload mid-flight).</summary>
    private sealed class CancelAfterReadsStream : MemoryStream
    {
        private readonly int _cancelAfterReads;
        private readonly CancellationTokenSource _cts;
        private int _reads;

        public CancelAfterReadsStream(byte[] payload, int reads, CancellationTokenSource cts) : base(payload)
        {
            _cancelAfterReads = reads;
            _cts = cts;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (++_reads > _cancelAfterReads) _cts.Cancel();   // the sender's next loop iteration observes it
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
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
