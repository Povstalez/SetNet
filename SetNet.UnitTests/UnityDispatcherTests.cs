using System.Threading;
using System.Threading.Tasks;
using SetNet.Unity;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the Unity main-thread dispatcher (no Unity dependency required).</summary>
public class UnityDispatcherTests
{
    [Fact]
    public void Drain_Runs_Posted_Actions_In_Order()
    {
        var dispatcher = new MainThreadDispatcher();
        var order = 0;
        int a = 0, b = 0;
        dispatcher.Post(() => a = ++order);
        dispatcher.Post(() => b = ++order);

        // Nothing runs until Drain (i.e. not on the posting thread).
        Assert.Equal(0, a);
        dispatcher.Drain();

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task PostAsync_Completes_On_Drain()
    {
        var dispatcher = new MainThreadDispatcher();
        var task = dispatcher.PostAsync(() => Thread.Sleep(1));
        Assert.False(task.IsCompleted);   // waits for a drain

        dispatcher.Drain();
        await task;                        // completes after drain
        Assert.True(task.IsCompletedSuccessfully);
    }
}
