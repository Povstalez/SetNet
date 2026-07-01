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

    [Fact]
    public async Task TryEnqueue_DropsAtCapacity_AndRecoversAfterDrain()
    {
        // Bounded queue (OOM protection): TryEnqueue accepts up to capacity, then drops; draining frees space.
        var q = new AsyncQueue<int>(capacity: 2);
        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));
        Assert.False(q.TryEnqueue(3)); // at capacity -> dropped

        var (ok, item) = await q.DequeueAsync();
        Assert.True(ok);
        Assert.Equal(1, item);

        Assert.True(q.TryEnqueue(4)); // space freed by the dequeue
    }

    [Fact]
    public void Enqueue_IgnoresCapacity()
    {
        // The unbounded Enqueue path (used by the accept queue) must never drop, even past the bound.
        var q = new AsyncQueue<int>(capacity: 1);
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3); // no throw, no drop
    }

    [Fact]
    public async Task TryEnqueue_Unbounded_NeverDrops()
    {
        var q = new AsyncQueue<int>(); // capacity 0 = unbounded
        for (int i = 0; i < 1000; i++)
            Assert.True(q.TryEnqueue(i));
        var (ok, first) = await q.DequeueAsync();
        Assert.True(ok);
        Assert.Equal(0, first);
    }
}
