using System;
using System.Threading.Tasks;
using SetNet.Auth;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the auth session store: reconnect-token rotation and background-sweep eviction.</summary>
public class SessionStoreTests
{
    [Fact]
    public void ReconnectToken_Rotates_On_Resume()
    {
        var store = new SessionStore(TimeSpan.FromMinutes(5));
        var session = store.Create("acc", null);
        var token1 = session.ReconnectToken;

        var resumed = store.Resume(token1, null);
        Assert.NotNull(resumed);
        var token2 = resumed!.ReconnectToken;

        Assert.NotEqual(token1, token2);                 // token rotated
        Assert.Null(store.Resume(token1, null));          // the old token is now dead (single-use)
        Assert.NotNull(store.Resume(token2, null));       // the new token resumes the same session
    }

    [Fact]
    public async Task Sweep_Evicts_Expired_Sessions()
    {
        var store = new SessionStore(TimeSpan.FromMilliseconds(1));
        var token = store.Create("acc", null).ReconnectToken;

        await Task.Delay(50);   // let it age past the tiny TTL
        store.Sweep();

        Assert.Null(store.Resume(token, null));           // swept away
    }

    [Fact]
    public void Live_Session_Survives_Sweep()
    {
        var store = new SessionStore(TimeSpan.FromMinutes(5));
        var token = store.Create("acc", null).ReconnectToken;

        store.Sweep();

        Assert.NotNull(store.Resume(token, null));        // still within TTL
    }
}
