using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

[MessageHandler(0)]
public class TestMessageFromServerHandler : IClientMessageHandler<TestMessage>
{
    public Task HandleAsync(TestMessage message)
    {
        Console.WriteLine("Message from server: " + message.Message);
        return Task.CompletedTask;
    }
}