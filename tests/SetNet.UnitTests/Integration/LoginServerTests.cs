using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.LoginServer;
using Xunit;

namespace SetNet.UnitTests.Integration
{
    /// <summary>
    /// End-to-end for the login coordinator: authenticate → server list → select → one-time token, then the "game server"
    /// consumes the token from the shared store (as it would after the client connects to it).
    /// </summary>
    [Collection("integration")]
    public class LoginServerTests
    {
        private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

        [Fact]
        public async Task Login_ServerList_Select_issues_a_one_time_token()
        {
            var tokens = new MemoryLoginTokenStore();   // in a cluster: a shared Redis/DB store

            var server = new TestServer(Config("login"));
            server.UseLoginServer(new LoginOptions
            {
                Authenticate = (u, p) => Task.FromResult(
                    u == "alice" && p == "secret" ? LoginAuth.Success("acc-1") : LoginAuth.Reject("bad credentials")),
                Servers = () => new[]
                {
                    new GameServerInfo { Id = "s1", Name = "Bartz", Host = "127.0.0.1", Port = 7777, Online = 10, Max = 100, Status = "good" },
                },
                Tokens = tokens,
            });
            _ = server.StartAsync();
            await Task.Delay(120);

            var client = new TestClient(Config("login"));
            var login = client.UseLogin();
            await client.ConnectAsync();

            // wrong password
            Assert.Equal(LoginStatus.InvalidCredentials, (await login.LoginAsync("alice", "wrong")).Status);

            // correct password
            Assert.True((await login.LoginAsync("alice", "secret")).Ok);

            // server list
            var servers = await login.ServerListAsync();
            Assert.Single(servers);
            Assert.Equal("Bartz", servers[0].Name);
            Assert.Equal(7777, servers[0].Port);

            // select → token + where to connect
            var sel = await login.SelectServerAsync("s1");
            Assert.True(sel.Ok);
            Assert.Equal("127.0.0.1", sel.Host);
            Assert.Equal(7777, sel.Port);
            Assert.False(string.IsNullOrEmpty(sel.Token));

            // the game server validates the token against the SAME shared store
            var consumed = await tokens.ConsumeAsync(sel.Token);
            Assert.NotNull(consumed);
            Assert.Equal("acc-1", consumed!.AccountId);
            Assert.Equal("s1", consumed.ServerId);

            // one-time: a second consume fails
            Assert.Null(await tokens.ConsumeAsync(sel.Token));
        }

        [Fact]
        public async Task Select_before_login_is_rejected()
        {
            var server = new TestServer(Config("login2"));
            server.UseLoginServer(new LoginOptions
            {
                Authenticate = (_, __) => Task.FromResult(LoginAuth.Success("acc-x")),
                Servers = () => new[] { new GameServerInfo { Id = "s1", Host = "h", Port = 1 } },
            });
            _ = server.StartAsync();
            await Task.Delay(120);

            var client = new TestClient(Config("login2"));
            var login = client.UseLogin();
            await client.ConnectAsync();

            var sel = await login.SelectServerAsync("s1");   // never logged in
            Assert.False(sel.Ok);
            Assert.Equal("not logged in", sel.Message);
        }
    }
}
