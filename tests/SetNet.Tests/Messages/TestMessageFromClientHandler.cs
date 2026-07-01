using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

[MessageHandler(0)]
public class TestMessageFromClientHandler : IServerMessageHandler<TestMessage>
{
    public Task HandleAsync(BasePeer peer, TestMessage message)
    {
        var player = (PlayerPeer)peer;

        Console.WriteLine($"Received from client: {message.Message}");

        player.TestSend();
        
        return Task.CompletedTask;
    }
}