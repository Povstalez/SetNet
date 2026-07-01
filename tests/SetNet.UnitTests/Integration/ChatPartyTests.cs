using System.Threading.Tasks;
using SetNet.Chat;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Party;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end tests for the Chat and Party modules over the in-memory transport.</summary>
[Collection("integration")]
public class ChatPartyTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Chat_Relays_To_Channel_Members()
    {
        var server = new TestServer(Config("chat"));
        server.UseChat();
        _ = server.StartAsync();
        await Task.Delay(120);

        var a = new TestClient(Config("chat"));
        var chatA = a.UseChat();
        string? gotFrom = null, gotText = null;
        chatA.MessageReceived += (ch, from, text) => { gotFrom = from; gotText = text; };
        await a.ConnectAsync();
        await chatA.JoinAsync("global");

        var b = new TestClient(Config("chat"));
        var chatB = b.UseChat();
        await b.ConnectAsync();
        await chatB.JoinAsync("global");
        await Task.Delay(100);
        await chatB.SendAsync("global", "hello");

        Assert.True(await WaitUntil(() => gotText == "hello"));
        Assert.NotNull(gotFrom);

        a.Disconnect(); b.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Party_Create_Join_And_Events()
    {
        var server = new TestServer(Config("party"));
        server.UseParties();
        _ = server.StartAsync();
        await Task.Delay(120);

        var a = new TestClient(Config("party"));
        var partyA = a.UseParty();
        string? joined = null;
        partyA.PlayerJoined += id => joined = id;
        await a.ConnectAsync();
        var info = await partyA.CreateAsync();
        Assert.NotEmpty(info.Code);
        Assert.Equal(info.OwnPlayerId, info.LeaderId);   // creator is leader

        var b = new TestClient(Config("party"));
        var partyB = b.UseParty();
        await b.ConnectAsync();
        var bInfo = await partyB.JoinAsync(info.Code);

        Assert.Equal(info.Code, bInfo.Code);
        Assert.Equal(2, bInfo.Members.Count);
        Assert.True(await WaitUntil(() => joined == bInfo.OwnPlayerId));   // A saw B join

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
