using System;
using System.Threading.Tasks;
using SetNet.Auth;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the default <see cref="MemorySessionStore"/>: token rotation and background-sweep eviction.</summary>
public class SessionStoreTests
{
    [Fact]
    public async Task ReconnectToken_Rotates_On_Resume()
    {
        ISessionStore store = new MemorySessionStore(TimeSpan.FromMinutes(5));
        var session = await store.CreateAsync("acc", null);
        var token1 = session.ReconnectToken;

        var resumed = await store.ResumeAsync(token1, null);
        Assert.NotNull(resumed);
        var token2 = resumed!.ReconnectToken;

        Assert.NotEqual(token1, token2);                       // token rotated
        Assert.Null(await store.ResumeAsync(token1, null));     // the old token is now dead (single-use)
        Assert.NotNull(await store.ResumeAsync(token2, null));  // the new token resumes the same session
    }

    [Fact]
    public async Task Sweep_Evicts_Expired_Sessions()
    {
        ISessionStore store = new MemorySessionStore(TimeSpan.FromMilliseconds(1));
        var token = (await store.CreateAsync("acc", null)).ReconnectToken;

        await Task.Delay(50);   // let it age past the tiny TTL
        await store.SweepAsync();

        Assert.Null(await store.ResumeAsync(token, null));      // swept away
    }

    [Fact]
    public async Task Live_Session_Survives_Sweep()
    {
        ISessionStore store = new MemorySessionStore(TimeSpan.FromMinutes(5));
        var token = (await store.CreateAsync("acc", null)).ReconnectToken;

        await store.SweepAsync();

        Assert.NotNull(await store.ResumeAsync(token, null));   // still within TTL
    }
}
