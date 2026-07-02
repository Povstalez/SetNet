using System;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.StateSync;
using SetNet.StateSync.NetworkVariable;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test for attribute-driven [SetNetVariable] fields: schema auto-build + server→client value binding.</summary>
[Collection("integration")]
public class NetworkVariableAttributeTests
{
    /// <summary>A POCO whose tagged fields become the replicated schema for archetype 55.</summary>
    [SetNetObject(55)]
    public sealed class MobState
    {
        [SetNetVariable] public int Health = 100;
        [SetNetVariable(Interpolate = true)] public Vec3 Position;
        [SetNetVariable] public string Name = "";
        [SetNetVariable] public MobKind Kind;   // enum → Int
    }

    public enum MobKind { Slime = 0, Dragon = 1 }

    static NetworkVariableAttributeTests()
    {
        NetworkVariables.Register<MobState>();   // reads [SetNetObject(55)]; identical on both ends (same process)
    }

    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Attribute_Fields_Build_Schema_And_Replicate()
    {
        var server = new TestServer(Config("nv-attr"));
        var world = server.UseStateSync(new StateSyncOptions { TickRate = 60 });
        _ = server.StartAsync();
        await Task.Delay(100);

        var client = new TestClient(Config("nv-attr"));
        var repl = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 0 });   // snap for determinism
        var mobs = repl.BindVariables<MobState>();
        await client.ConnectAsync();
        await Task.Delay(100);

        // Server-side: mutate the POCO, then Push so the values are sampled on the next tick.
        var bound = world.SpawnBound(new MobState { Health = 42, Position = new Vec3(1, 2, 3), Name = "boss", Kind = MobKind.Dragon });

        Assert.True(await WaitUntil(() => { repl.Update(); mobs.Pull(); return mobs.Values.Any(); }));
        var mob = mobs.Values.First();
        Assert.Equal(42, mob.Health);
        Assert.Equal("boss", mob.Name);
        Assert.Equal(MobKind.Dragon, mob.Kind);
        Assert.True(Approx(mob.Position, new Vec3(1, 2, 3)));

        // Update a field on the server and confirm it flows through.
        bound.Target.Health = 17;
        bound.Push();
        Assert.True(await WaitUntil(() => { repl.Update(); mobs.Pull(); return mob.Health == 17; }));

        client.Disconnect();
        await server.StopAsync();
    }

    private static bool Approx(Vec3 a, Vec3 b, float eps = 0.01f)
        => Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps && Math.Abs(a.Z - b.Z) < eps;

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
