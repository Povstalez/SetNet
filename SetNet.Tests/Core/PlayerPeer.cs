using SetNet.Config;
using SetNet.Core;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests;

public class PlayerPeer : BasePeer
{
    public PlayerPeer(PeerInfo currentPeerInfo) : base(currentPeerInfo)
    {
        Console.WriteLine("Configurate new Peer on server");
    }

    public void TestSend()
    {
        SendAsync( 0, new TestMessage()
        {
            Message = "Hello from server!"
        });
    }

    protected override void RegisterDataHandlers()
    {
        base.RegisterDataHandlers();
        // RegisterDataHandler((ushort)MessageTypes.PositionChanged, OnPositionChanged);
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine($"[Server] Client {CurrentPeerInfo.Id} disconnected.");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"[Server] {error}");
    }

    protected override void OnUnexpectedDisconnect()
    {
        Console.WriteLine($"[Server] Client {CurrentPeerInfo.Id} unexpectedly disconnected!");
    }

    private async Task OnPositionChanged(byte[] obj)
    {
        var message = SetNetSerializer.Deserialize<TestMessage>(obj);
        Console.WriteLine($"Received message on server. Message: {message.Message}");
    }
}