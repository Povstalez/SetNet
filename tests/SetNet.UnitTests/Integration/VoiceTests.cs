using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SetNet.Config;
using SetNet.InMemory;
using SetNet.Voice;
using Xunit;

namespace SetNet.UnitTests.Integration;

/// <summary>End-to-end test for the voice relay: a frame sent to a channel reaches the other member.</summary>
[Collection("integration")]
public class VoiceTests
{
    private static Configuration Config(string key) => new Configuration { Host = key, Port = 1 }.UseInMemory();

    [Fact]
    public async Task Frame_Is_Relayed_To_Channel_Members()
    {
        var server = new TestServer(Config("voice"));
        server.UseVoice();
        _ = server.StartAsync();
        await Task.Delay(150);

        var received = new ConcurrentQueue<(uint speaker, ushort channel, byte[] audio)>();

        var a = new TestClient(Config("voice"));
        var voiceA = a.UseVoice();
        await a.ConnectAsync();
        await voiceA.JoinChannel(1);

        var b = new TestClient(Config("voice"));
        var voiceB = b.UseVoice();
        voiceB.FrameReceived += (s, ch, audio) => received.Enqueue((s, ch, audio));
        await b.ConnectAsync();
        await voiceB.JoinChannel(1);
        await Task.Delay(120);

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await voiceA.SendFrame(1, payload);

        Assert.True(await WaitUntil(() => received.Count > 0));
        Assert.Contains(received, r => r.channel == 1 && r.audio.Length == payload.Length && r.audio[0] == 1 && r.audio[4] == 5);

        a.Disconnect(); b.Disconnect();
        await server.StopAsync();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
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
