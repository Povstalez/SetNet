using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

[MessageHandler(0)]
public class TestMessageFromClientHandler : IServerMessageHandler
{
    public Task HandleAsync(BasePeer peer, byte[] data)
    {
        var player = (PlayerPeer)peer;
        
        var message = SetNetSerializer.Deserialize<TestMessage>(data);
        Console.WriteLine($"Received from client: {message.Message}");

        player.TestSend();
        
        return Task.CompletedTask;
    }
}