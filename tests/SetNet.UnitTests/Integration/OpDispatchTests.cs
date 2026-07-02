using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MessagePack;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Protocol;
using Xunit;

namespace SetNet.UnitTests.Integration;

// A test channel id well above the 1–24 range used by the real modules.
internal static class OpTestChannel { public const ushort Id = 700; }

/// <summary>Captures fire-and-forget ops the op-dispatch test service received, and client-side push events.</summary>
public static class OpDispatchInbox
{
    public static readonly ConcurrentQueue<string> Notes = new();
    public static readonly ConcurrentQueue<string> Events = new();
}

[MessagePackObject]
public class AddReq { [Key(0)] public int A { get; set; } [Key(1)] public int B { get; set; } }

[MessagePackObject]
public class SumResp { [Key(0)] public int Sum { get; set; } }

[MessagePackObject]
public class NoteReq { [Key(0)] public string Text { get; set; } = ""; }

/// <summary>
/// A channel written as one method per op (no <c>switch</c>, no <see cref="IChannelService"/>). Exercises every
/// reply shape: typed request/reply, raw request/reply, fire-and-forget (no reply), and the thrown-error path.
/// </summary>
[ProtocolChannel(OpTestChannel.Id)]
public sealed class OpDispatchTestService
{
    [Op(1)]
    public SumResp Add(AddReq req) => new SumResp { Sum = req.A + req.B };   // typed request → typed reply

    [Op(2)]
    public Task<byte[]> Echo(byte[] body) => Task.FromResult(body);          // raw async request → raw reply

    [Op(3)]
    public void Note(NoteReq req) => OpDispatchInbox.Notes.Enqueue(req.Text); // fire-and-forget, no reply

    [Op(4)]
    public int Boom() => throw new InvalidOperationException("kaboom");       // failure → error reply

    [Op(5)]
    public Task Ping(BasePeer peer) => peer.PublishAsync(OpTestChannel.Id, 20, new NoteReq { Text = "pong" }); // fire-and-forget → server push
}

/// <summary>Client-side push handler declared via <c>[Event]</c> (the attribute analog of <c>client.On&lt;T&gt;</c>).</summary>
[ProtocolChannel(OpTestChannel.Id)]
public sealed class OpTestClientEvents
{
    [Event(20)]
    public void OnPong(NoteReq e) => OpDispatchInbox.Events.Enqueue("attr:" + e.Text);
}

/// <summary>End-to-end test that <c>[Op]</c> methods on a <c>[ProtocolChannel]</c> class are auto-dispatched.</summary>
[Collection("integration")]
public class OpDispatchTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task OpMethods_Dispatch_All_Shapes()
    {
        OpDispatchInbox.Notes.Clear();
        OpDispatchInbox.Events.Clear();

        var server = new TestServer(Config(5931));
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5931));
        await client.ConnectAsync();

        // typed request → typed reply
        var sum = await client.RequestAsync<AddReq, SumResp>(OpTestChannel.Id, 1, new AddReq { A = 2, B = 3 });
        Assert.Equal(5, sum.Sum);

        // raw request → raw reply
        var echoed = await client.RequestRawAsync(OpTestChannel.Id, 2, new byte[] { 9, 8, 7 });
        Assert.Equal(new byte[] { 9, 8, 7 }, echoed);

        // fire-and-forget (no reply)
        await client.PostAsync(OpTestChannel.Id, 3, new NoteReq { Text = "hi" });
        Assert.True(await WaitUntil(() => OpDispatchInbox.Notes.Contains("hi")));

        // thrown handler exception → ProtocolException on the caller
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => client.RequestRawAsync(OpTestChannel.Id, 4));
        Assert.Contains("kaboom", ex.Message);

        // server push → BOTH the [Event] attribute handler AND an imperative On<T> fire for the same (channel, op)
        using var sub = client.On<NoteReq>(OpTestChannel.Id, 20, e => OpDispatchInbox.Events.Enqueue("on:" + e.Text));
        await client.PostRawAsync(OpTestChannel.Id, 5);
        Assert.True(await WaitUntil(() =>
            OpDispatchInbox.Events.Contains("attr:pong") && OpDispatchInbox.Events.Contains("on:pong")));

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (start.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
