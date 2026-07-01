using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Auth;
using SetNet.Config;
using SetNet.Core.Transport;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>Test authenticator: accepts any non-empty token except "bad"; the account id equals the token.</summary>
public class TestAuthenticator : IAuthenticator
{
    /// <inheritdoc/>
    public Task<AuthResult> AuthenticateAsync(string token)
        => Task.FromResult(string.IsNullOrEmpty(token) || token == "bad"
            ? AuthResult.Fail("bad token")
            : AuthResult.Ok(token));
}

/// <summary>End-to-end tests for the SetNet.Auth enforced gate, token validation, and multi-session policy.</summary>
[Collection("integration")]
public class AuthTests
{
    private static Configuration Config(int port) => new Configuration
    {
        Host = "127.0.0.1",
        Port = port,
        TransportType = TransportType.Tcp
    };

    [Fact]
    public async Task Gate_Blocks_Unauthenticated_Then_Allows_After_Auth()
    {
        TestInbox.Reset();
        var server = new TestServer(Config(5881));
        server.UseAuth(new TestAuthenticator());
        _ = server.StartAsync();
        await Task.Delay(200);

        // Unauthenticated client: its application message must be dropped by the gate.
        var noauth = new TestClient(Config(5881));
        await noauth.ConnectAsync();
        await noauth.SendEchoAsync("blocked", DeliveryMethod.Reliable);
        await Task.Delay(300);
        Assert.DoesNotContain("blocked", TestInbox.ServerReceived);

        // Authenticated client: after login, the same message gets through.
        var authed = new TestClient(Config(5881));
        var auth = authed.UseAuth("good");
        await authed.ConnectAsync();
        await auth.WhenAuthenticated;
        await authed.SendEchoAsync("allowed", DeliveryMethod.Reliable);

        Assert.True(await WaitUntil(() => TestInbox.ServerReceived.Contains("allowed")));
        Assert.DoesNotContain("blocked", TestInbox.ServerReceived);

        noauth.Disconnect();
        authed.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Auth_Rejects_Bad_Token()
    {
        var server = new TestServer(Config(5882));
        server.UseAuth(new TestAuthenticator());
        _ = server.StartAsync();
        await Task.Delay(200);

        var client = new TestClient(Config(5882));
        var auth = client.UseAuth("bad");
        await client.ConnectAsync();

        await Assert.ThrowsAsync<AuthException>(() => auth.WhenAuthenticated);

        client.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task KickExisting_Disconnects_Previous_Session()
    {
        var server = new TestServer(Config(5883));
        server.UseAuth(new TestAuthenticator(), new AuthOptions { MultiSession = MultiSessionPolicy.KickExisting });
        _ = server.StartAsync();
        await Task.Delay(200);

        var a = new CountingClient(Config(5883));
        var authA = a.UseAuth("acc1");
        await a.ConnectAsync();
        await authA.WhenAuthenticated;

        // Second login under the same account must kick the first session.
        var b = new TestClient(Config(5883));
        var authB = b.UseAuth("acc1");
        await b.ConnectAsync();
        await authB.WhenAuthenticated;

        Assert.True(await WaitUntil(() => System.Threading.Volatile.Read(ref a.DisconnectedCount) >= 1));

        b.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
