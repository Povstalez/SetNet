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
        SendAsync((ushort)MessageTypes.PositionChanged, message);
    }
    
    private void OnUpdateClientId(byte[] obj)
    {
        var message = MessagePackSerializer.Deserialize<UpdateClientIdMessage>(obj);
        Console.WriteLine($"Update ClientId. {message.ClientId}");

        var testMsg = new TestMessage();
        testMsg.Message = "Test from client";
        SendAsync((ushort)MessageTypes.PositionChanged, testMsg);
    }

    protected override void OnConnected()
    {
        Console.WriteLine("Connected to server");

        SendAsync(0, new TestMessage()
        {
            Message = "Hello from client!"
        });

    }

    protected override void OnDisconnected()
    {
        Console.WriteLine("Disconnected from server");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"Error: {error}");
    }

    protected override void OnStateChanged(ConnectionState from, ConnectionState to)
    {
        Console.WriteLine($"[State] {from} → {to}");
    }

    protected override void OnReconnecting(int attempt, int maxAttempts)
    {
        Console.WriteLine($"[Reconnect] Attempt {attempt}/{maxAttempts}...");
    }

    protected override void OnReconnected()
    {
        Console.WriteLine("[Reconnect] Successfully reconnected!");
    }

    protected override void OnReconnectFailed()
    {
        Console.WriteLine("[Reconnect] All attempts failed.");
    }
}