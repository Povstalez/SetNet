using MessagePack;
using SetNet.Config;
using SetNet.Core;
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
        Console.WriteLine($"Client {CurrentPeerInfo.Id} disconnected.");
    }

    private async Task OnPositionChanged(byte[] obj)
    {
        var message = MessagePackSerializer.Deserialize<TestMessage>(obj);
        Console.WriteLine($"Received message on server. Message: {message.Message}");
    }
}