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
        SendAsync((ushort) MessageTypes.UpdateClientId, new UpdateClientIdMessage()
        {
            ClientId = CurrentPeerInfo.Id
        });
    }

    protected override void RegisterDataHandlers()
    {
        RegisterDataHandler((ushort)MessageTypes.PositionChanged, OnPositionChanged);
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine($"Client {CurrentPeerInfo.Id} disconnected.");
    }

    private async Task OnPositionChanged(byte[] obj)
    {
        var message = MessagePackSerializer.Deserialize<TestMessage>(obj);
        Console.WriteLine($"Received message on server. X: {message.X}, Y: {message.Y}");
    }
}