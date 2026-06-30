using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

[MessageHandler(0)]
public class TestMessageFromServerHandler : IClientMessageHandler
{
    public Task HandleAsync(byte[] data)
    {
        var message = SetNetSerializer.Deserialize<TestMessage>(data);
        Console.WriteLine("Message from server: " + message.Message);
        return Task.CompletedTask;
    }
}