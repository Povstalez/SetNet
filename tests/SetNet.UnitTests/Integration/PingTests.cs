using System;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.Core;
using SetNet.InMemory;
using SetNet.Ping;
using Xunit;

namespace SetNet.UnitTests.Integration
{
    /// <summary>End-to-end for SetNet.Ping: the server measures each peer's RTT, and the client can measure its own RTT to the server.</summary>
    [Collection("integration")]
    public class PingTests
    {
        private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

        [Fact]
        public async Task Server_measures_peer_ping_and_client_measures_its_own()
        {
            var server = new TestServer(Config("ping"));
            var ping = server.UsePing(new PingOptions { IntervalMs = 40 });   // ping peers ~25x/sec

            BasePeer? peer = null; double serverRtt = -1;
            ping.Updated += (p, rtt) => { peer = p; serverRtt = rtt; };

            _ = server.StartAsync();
            await Task.Delay(120);

            var client = new TestClient(Config("ping"));
            var pc = client.UsePing();
            await client.ConnectAsync();

            // the server pings the peer and records the round trip
            Assert.True(await WaitUntil(() => serverRtt >= 0), "server never measured the peer's ping");
            Assert.NotNull(peer);
            Assert.True(ping.Of(peer!) >= 0);

            // the client measures its own ping to the server
            await pc.MeasureAsync();
            Assert.True(await WaitUntil(() => pc.Last >= 0), "client never measured its own ping");

            pc.Dispose();
            ping.Dispose();
        }

        private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }
    }
}
