using System.Threading.Tasks;
using SetNet.Core.Transport.Udp;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the single-consumer async FIFO <see cref="AsyncQueue{T}"/>.</summary>
public class AsyncQueueTests
{
    [Fact]
    public async Task Enqueue_Dequeue_PreservesOrder()
    {
        var q = new AsyncQueue<string>();
        q.Enqueue("a");
        q.Enqueue("b");

        var first = await q.DequeueAsync();
        var second = await q.DequeueAsync();
        Assert.True(first.ok);
        Assert.Equal("a", first.item);
        Assert.True(second.ok);
        Assert.Equal("b", second.item);
    }

    [Fact]
    public async Task Complete_SignalsEofAsNotOk()
    {
        var q = new AsyncQueue<string>();
        q.Complete();

        var (ok, _) = await q.DequeueAsync();
        Assert.False(ok);
    }

    [Fact]
    public async Task DequeueAsync_WaitsForLaterEnqueue()
    {
        var q = new AsyncQueue<string>();
        var pending = q.DequeueAsync();
        Assert.False(pending.IsCompleted);

        q.Enqueue("later");
        var (ok, item) = await pending;
        Assert.True(ok);
        Assert.Equal("later", item);
    }
}
