using SetNet.Config;
using SetNet.Core;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests;

public class MainClient(Configuration config) : BaseClient(config)
{
    public void DisconnectFromServer()
    {
        Disconnect();
    }
    
    private void OnPositionChanged(byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<TestMessage>(data);
        Console.WriteLine($"Received message on client. X: {message.X}, Y: {message.Y}");

        message.X = 4444.55f;
        message.Y = 5555.44f;
        SendAsync((ushort)MessageTypes.PositionChanged, message);
    }
    
    private void OnUpdateClientId(byte[] obj)
    {
        var message = MessagePackSerializer.Deserialize<UpdateClientIdMessage>(obj);
        Console.WriteLine($"Update ClientId. {message.ClientId}");

        var testMsg = new TestMessage();
        testMsg.X = 4444.55f;
        testMsg.Y = 5555.44f;
        SendAsync((ushort)MessageTypes.PositionChanged, testMsg);
    }

    protected override void RegisterDataHandlers()
    {
        RegisterDataHandler((ushort)MessageTypes.PositionChanged, OnPositionChanged);
        RegisterDataHandler((ushort)MessageTypes.UpdateClientId, OnUpdateClientId);
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine("Disconnected from server");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"Error: {error}");
    }
}