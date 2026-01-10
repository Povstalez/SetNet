using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

[MessageHandler(0)]
public class TestMessageFromServerHandler : IClientMessageHandler
{
    public Task HandleAsync(byte[] data)
    {
        var message = MessagePack.MessagePackSerializer.Deserialize<TestMessage>(data);
        Console.WriteLine("Message from server: " + message.Message);
        return Task.CompletedTask;
    }
}