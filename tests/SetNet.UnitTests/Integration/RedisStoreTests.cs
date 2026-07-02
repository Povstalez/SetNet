using System;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Redis;
using StackExchange.Redis;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// Tests the Redis-backed stores against a real Redis at localhost:6379. Each test is skipped (not failed) when no
/// Redis is reachable, so CI without Redis stays green while a dev with Redis running gets real coverage.
/// </summary>
[Collection("integration")]
public class RedisStoreTests
{
    private static IConnectionMultiplexer? TryConnect()
    {
        try
        {
            var mux = ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { "localhost:6379" },
                AbortOnConnectFail = false,
                ConnectTimeout = 500,
            });
            return mux.IsConnected ? mux : null;
        }
        catch { return null; }
    }

    // Unique prefix per run so tests don't collide with real data or each other.
    private static string Prefix() => $"setnettest:{Guid.NewGuid():N}:";

    [SkippableFact]
    public void BanStore_Ban_Check_Unban()
    {
        using var mux = TryConnect();
        Skip.If(mux == null, "No Redis at localhost:6379");

        var store = new RedisBanStore(mux!, Prefix());
        Assert.False(store.IsBanned("1.2.3.4"));
        store.Ban("1.2.3.4");
        Assert.True(store.IsBanned("1.2.3.4"));
        store.Unban("1.2.3.4");
        Assert.False(store.IsBanned("1.2.3.4"));
    }

    [SkippableFact]
    public async Task SessionStore_Create_Resume_Rotates_Token()
    {
        using var mux = TryConnect();
        Skip.If(mux == null, "No Redis at localhost:6379");

        var store = new RedisSessionStore(mux!, TimeSpan.FromMinutes(5), Prefix());
        var created = await store.CreateAsync("acct-1", null);
        Assert.Equal("acct-1", created.AccountId);

        var resumed = await store.ResumeAsync(created.ReconnectToken, null);
        Assert.NotNull(resumed);
        Assert.Equal(created.SessionId, resumed!.SessionId);
        Assert.NotEqual(created.ReconnectToken, resumed.ReconnectToken);   // token rotated

        // Old token is now single-use / invalid.
        Assert.Null(await store.ResumeAsync(created.ReconnectToken, null));

        var forAccount = await store.SessionsForAccountAsync("acct-1");
        Assert.Contains(forAccount, s => s.SessionId == created.SessionId);

        await store.RemoveAsync(resumed);
        Assert.Null(await store.ResumeAsync(resumed.ReconnectToken, null));
    }
}
