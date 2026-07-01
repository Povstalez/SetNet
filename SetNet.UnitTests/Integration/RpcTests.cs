using System;
using System.Threading.Tasks;
using MessagePack;
using SetNet.Config;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Rpc;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Request/response message for the RPC tests.</summary>
[MessagePackObject]
public class RpcMsg
{
    /// <summary>Arbitrary text payload.</summary>
    [Key(0)] public string Text { get; set; } = "";
}

/// <summary>Echoes the request text upper-cased.</summary>
[RpcMethod(1)]
public class EchoRpc : IRpcHandler<RpcMsg, RpcMsg>
{
    /// <inheritdoc/>
    public Task<RpcMsg> HandleAsync(BasePeer peer, RpcMsg req)
        => Task.FromResult(new RpcMsg { Text = req.Text.ToUpperInvariant() });
}

/// <summary>Always throws, to verify server-side errors surface as <see cref="RpcException"/> on the caller.</summary>
[RpcMethod(2)]
public class FailRpc : IRpcHandler<RpcMsg, RpcMsg>
{
    /// <inheritdoc/>
    public Task<RpcMsg> HandleAsync(BasePeer peer, RpcMsg req) => throw new InvalidOperationException("boom");
}

/// <summary>Responds only after a delay, to verify the caller's timeout fires.</summary>
[RpcMethod(3)]
public class SlowRpc : IRpcHandler<RpcMsg, RpcMsg>
{
    /// <inheritdoc/>
    public async Task<RpcMsg> HandleAsync(BasePeer peer, RpcMsg req)
    {
        await Task.Delay(500);
        return req;
    }
}

/// <summary>End-to-end tests for the SetNet.Rpc request/response layer over a real loopback connection.</summary>
[Collection("integration")]
public class RpcTests
{
    private static async Task<(TestServer server, TestClient client)> ConnectAsync(int port)
    {
        var server = new TestServer(new Configuration { Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp });
        _ = server.StartAsync();
        await Task.Delay(200);
        var client = new TestClient(new Configuration { Host = "127.0.0.1", Port = port, TransportType = TransportType.Tcp });
        await client.ConnectAsync();
        return (server, client);
    }

    [Fact]
    public async Task Call_RoundTrips_TypedResponse()
    {
        var (server, client) = await ConnectAsync(5871);
        try
        {
            var reply = await client.CallAsync<RpcMsg, RpcMsg>(1, new RpcMsg { Text = "hello" });
            Assert.Equal("HELLO", reply.Text);
        }
        finally { client.Disconnect(); await server.StopAsync(); }
    }

    [Fact]
    public async Task Call_ServerThrows_SurfacesAsRpcException()
    {
        var (server, client) = await ConnectAsync(5872);
        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(
                () => client.CallAsync<RpcMsg, RpcMsg>(2, new RpcMsg { Text = "x" }));
            Assert.Contains("boom", ex.Message);
        }
        finally { client.Disconnect(); await server.StopAsync(); }
    }

    [Fact]
    public async Task Call_UnknownMethod_SurfacesAsRpcException()
    {
        var (server, client) = await ConnectAsync(5873);
        try
        {
            await Assert.ThrowsAsync<RpcException>(
                () => client.CallAsync<RpcMsg, RpcMsg>(99, new RpcMsg { Text = "x" }));
        }
        finally { client.Disconnect(); await server.StopAsync(); }
    }

    [Fact]
    public async Task Call_TimesOut_WhenHandlerIsSlow()
    {
        var (server, client) = await ConnectAsync(5874);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => client.CallAsync<RpcMsg, RpcMsg>(3, new RpcMsg { Text = "x" }, timeoutMs: 50));
        }
        finally { client.Disconnect(); await server.StopAsync(); }
    }
}
