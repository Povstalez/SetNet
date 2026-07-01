using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Commands;
using SetNet.Data;
using SetNet.Data.Attributes;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for reflection-based discovery of typed handlers in <see cref="ServerCommandExecutor"/>.</summary>
public class CommandExecutorTests
{
    [Fact]
    public void Discovers_DecoratedTypedHandlerInThisAssembly()
    {
        var executor = new ServerCommandExecutor();

        // The probe handler implements IServerMessageHandler<DiscoveryProbeMessage> and is decorated with
        // [MessageHandler(700)], so discovery must register it under that wire type id.
        Assert.Contains((ushort)700, executor.Keys);
    }
}

/// <summary>A throwaway message used only by the discovery probe handler.</summary>
public class DiscoveryProbeMessage
{
    public int Value { get; set; }
}

/// <summary>A throwaway handler that exists solely so <see cref="CommandExecutorTests"/> can verify discovery.</summary>
[MessageHandler(700)]
public class DiscoveryProbeHandler : IServerMessageHandler<DiscoveryProbeMessage>
{
    /// <summary>No-op handler body; discovery, not behavior, is what the test checks.</summary>
    public Task HandleAsync(BasePeer peer, DiscoveryProbeMessage message) => Task.CompletedTask;
}
