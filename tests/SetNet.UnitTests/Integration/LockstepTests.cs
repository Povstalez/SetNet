using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Lockstep;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test that lockstep delivers every participant's TYPED input for a finalized turn.</summary>
[Collection("integration")]
public class LockstepTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Turn_Delivers_All_Typed_Inputs()
    {
        var server = new TestServer(Config("ls"));
        server.UseLockstep(new LockstepOptions { TurnTimeoutMs = 2000 });   // rely on wait-for-all, not the timeout
        _ = server.StartAsync();
        await Task.Delay(120);

        var a = new TestClient(Config("ls"));
        var lsA = a.UseLockstep<int>();                 // typed input, no byte[]
        IReadOnlyDictionary<string, int>? received = null;
        lsA.TurnReady += (turn, inputs) => received = inputs;
        await a.ConnectAsync();

        var b = new TestClient(Config("ls"));
        var lsB = b.UseLockstep<int>();
        await b.ConnectAsync();
        await Task.Delay(120);                          // both enrolled as participants

        lsA.SubmitInput(11);
        lsB.SubmitInput(22);                            // all inputs in → turn finalizes immediately

        Assert.True(await WaitUntil(() => received != null && received.Count == 2));
        Assert.Contains(11, received!.Values);
        Assert.Contains(22, received!.Values);

        a.Disconnect(); b.Disconnect();
        await server.StopAsync();
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
