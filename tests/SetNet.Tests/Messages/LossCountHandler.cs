using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using SetNet.Tests.Data;

namespace SetNet.Tests.Messages;

/// <summary>Counts messages received under the simulated-loss / Both-mode routing scenarios.</summary>
[MessageHandler((ushort)MessageTypes.LossPing)]
public class LossCountHandler : IServerMessageHandler<LossCountMessage>
{
    public Task HandleAsync(BasePeer peer, LossCountMessage message)
    {
        LossStats.Increment(message.ViaReliable);
        return Task.CompletedTask;
    }
}
