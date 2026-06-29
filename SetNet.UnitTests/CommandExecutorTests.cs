using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Commands;
using SetNet.Data;
using SetNet.Data.Attributes;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for reflection-based handler discovery in <see cref="CommandExecutor{T}"/>.</summary>
public class CommandExecutorTests
{
    [Fact]
    public void Discovers_DecoratedHandlerInThisAssembly()
    {
        var executor = new CommandExecutor<IServerMessageHandler>();

        Assert.Contains((ushort)700, executor.Keys);
        Assert.IsType<DiscoveryProbeHandler>(executor.Handlers[700]);
    }
}

/// <summary>A throwaway handler that exists solely so <see cref="CommandExecutorTests"/> can verify discovery.</summary>
[MessageHandler(700)]
public class DiscoveryProbeHandler : IServerMessageHandler
{
    /// <summary>No-op handler body; discovery, not behavior, is what the test checks.</summary>
    public Task HandleAsync(BasePeer peer, byte[] data) => Task.CompletedTask;
}
