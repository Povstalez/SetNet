using System;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core.Transport;
using SetNet.InMemory;
using SetNet.ProofOfWork;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for the proof-of-work admission gate (low difficulty so solving is instant).</summary>
[Collection("integration")]
public class ProofOfWorkTests
{
    private const int Difficulty = 10;   // ~1024 hashes — negligible, but exercises the whole handshake

    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Solved_Client_Frames_Pass()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("pow-ok"));
        server.UseProofOfWork(Difficulty);
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("pow-ok"));
        client.UseProofOfWork();                               // auto-solves on connect
        await client.ConnectAsync();
        await Task.Delay(300);                                 // give the solver time to answer

        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);
        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("hi")));

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Unsolved_Client_Frames_Are_Dropped()
    {
        TestInbox.Reset();
        var server = new TestServer(Config("pow-drop"));
        server.UseProofOfWork(Difficulty);
        _ = server.StartAsync();
        await Task.Delay(150);

        var client = new TestClient(Config("pow-drop"));       // NOTE: does not call UseProofOfWork → never solves
        await client.ConnectAsync();
        await Task.Delay(200);
        await client.SendEchoAsync("hi", DeliveryMethod.Reliable);
        await Task.Delay(300);

        Assert.Empty(TestInbox.ServerReceived);                // gate dropped it

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
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
