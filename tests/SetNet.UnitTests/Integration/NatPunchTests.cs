using System;
using System.Net;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.NatPunch;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>
/// End-to-end tests for NAT punch-through: the coordinator exchanges endpoint candidates between host and
/// guest, and the puncher opens a real UDP path (loopback stands in for the punched hole).
/// </summary>
[Collection("integration")]
public class NatPunchTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Coordinator_Exchanges_Endpoint_Candidates()
    {
        var server = new TestServer(Config("natpunch"));
        server.UseNatPunch();
        _ = server.StartAsync();
        await Task.Delay(120);

        var hostClient = new TestClient(Config("natpunch"));
        var hostPunch = hostClient.UseNatPunch();
        await hostClient.ConnectAsync();

        var code = await hostPunch.RegisterAsync(udpPort: 41001);
        Assert.False(string.IsNullOrEmpty(code));

        var guestClient = new TestClient(Config("natpunch"));
        var guestPunch = guestClient.UseNatPunch();
        await guestClient.ConnectAsync();

        var hostSide = hostPunch.WaitForGuestAsync();
        var guestTarget = await guestPunch.PunchAsync(code, udpPort: 41002);
        var hostTarget = await hostSide;

        // Each side sees the counterpart's reported UDP port in the public candidate.
        // (The in-memory transport has no remote endpoint, so the coordinator can only build the port half —
        // over TCP/UDP transports the address comes from peer.RemoteEndPoint.)
        Assert.True(hostTarget.IsHost == false);    // host received the *guest's* candidates
        Assert.True(guestTarget.IsHost);            // guest received the *host's* candidates

        // Private candidates carry the reported ports.
        Assert.All(guestTarget.PrivateEndPoints, ep => Assert.Equal(41001, ep.Port));
        Assert.All(hostTarget.PrivateEndPoints, ep => Assert.Equal(41002, ep.Port));

        // Unknown code is rejected.
        await Assert.ThrowsAsync<NatPunchException>(() => guestPunch.PunchAsync("ZZZZZZ", 41002));

        hostClient.Disconnect(); guestClient.Disconnect();
        await server.StopAsync();
    }

    [Fact]
    public async Task Puncher_Opens_A_Udp_Path_On_Loopback()
    {
        // Two punchers aimed at each other on loopback — the same simultaneous-probe dance as across NATs.
        var targetA = new NatPunchTarget { PublicEndPoint = new IPEndPoint(IPAddress.Loopback, 42002) };
        var targetB = new NatPunchTarget { PublicEndPoint = new IPEndPoint(IPAddress.Loopback, 42001) };

        var punchA = NatPuncher.TryPunchAsync(42001, targetA, timeoutMs: 5000);
        var punchB = NatPuncher.TryPunchAsync(42002, targetB, timeoutMs: 5000);
        var results = await Task.WhenAll(punchA, punchB);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal(42002, results[0]!.Port);
        Assert.Equal(42001, results[1]!.Port);
    }

    [Fact]
    public async Task Punch_Times_Out_When_Nobody_Answers()
    {
        var silent = new NatPunchTarget { PublicEndPoint = new IPEndPoint(IPAddress.Loopback, 42999) };
        var result = await NatPuncher.TryPunchAsync(42998, silent, timeoutMs: 700);
        Assert.Null(result);
    }
}
