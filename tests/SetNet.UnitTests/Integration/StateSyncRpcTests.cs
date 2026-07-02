using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using SetNet.StateSync.Rpc;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test of entity-scoped RPCs in both directions over the in-memory transport.</summary>
[Collection("integration")]
public class StateSyncRpcTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Rpc_Round_Trips_Both_Directions()
    {
        BasePeer? peer = null;
        var server = new TestServer(Config("ssrpc"));
        server.PeerConnected += p => peer = p;
        var serverRpc = server.UseStateSyncRpc();
        (uint netId, ushort method, byte[] payload) fromClient = default;
        serverRpc.Received += (p, netId, m, data) => fromClient = (netId, m, data);
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("ssrpc"));
        var clientRpc = client.UseStateSyncRpc();
        (uint netId, ushort method, byte[] payload) fromServer = default;
        clientRpc.Received += (netId, m, data) => fromServer = (netId, m, data);
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => peer != null));

        // client → server
        await clientRpc.SendAsync(netId: 42, methodId: 7, payload: new byte[] { 1, 2 });
        Assert.True(await WaitUntil(() => fromClient.netId == 42));
        Assert.Equal(7, fromClient.method);
        Assert.Equal(new byte[] { 1, 2 }, fromClient.payload);

        // server → client
        await serverRpc.SendAsync(peer!, netId: 99, methodId: 3, payload: new byte[] { 9 });
        Assert.True(await WaitUntil(() => fromServer.netId == 99));
        Assert.Equal(3, fromServer.method);
        Assert.Equal(new byte[] { 9 }, fromServer.payload);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Typed_On_Handlers_Deserialize_Per_Method()
    {
        const ushort GreetMethod = 5;
        BasePeer? peer = null;
        var server = new TestServer(Config("ssrpc-typed"));
        server.PeerConnected += p => peer = p;
        var serverRpc = server.UseStateSyncRpc();
        (uint netId, string text) gotOnServer = default;
        serverRpc.On<EchoMessage>(GreetMethod, (p, netId, msg) => gotOnServer = (netId, msg.Text));   // typed, no byte[]
        _ = server.StartAsync();
        await Task.Delay(120);

        var client = new TestClient(Config("ssrpc-typed"));
        var clientRpc = client.UseStateSyncRpc();
        (uint netId, string text) gotOnClient = default;
        clientRpc.On<EchoMessage>(GreetMethod, (netId, msg) => gotOnClient = (netId, msg.Text));
        await client.ConnectAsync();
        Assert.True(await WaitUntil(() => peer != null));

        await clientRpc.SendAsync(netId: 1, GreetMethod, new EchoMessage { Text = "hi-server" });   // typed send
        Assert.True(await WaitUntil(() => gotOnServer.netId == 1));
        Assert.Equal("hi-server", gotOnServer.text);

        await serverRpc.SendAsync(peer!, netId: 2, GreetMethod, new EchoMessage { Text = "hi-client" });
        Assert.True(await WaitUntil(() => gotOnClient.netId == 2));
        Assert.Equal("hi-client", gotOnClient.text);

        client.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(System.Func<bool> condition, int timeoutMs = 4000)
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
