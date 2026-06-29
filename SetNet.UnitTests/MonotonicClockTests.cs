using System.Threading.Tasks;
using SetNet.Core;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the <see cref="MonotonicClock"/> elapsed-time helper.</summary>
public class MonotonicClockTests
{
    [Fact]
    public async Task ElapsedMs_ReflectsWallDelay()
    {
        var t0 = MonotonicClock.Timestamp;
        await Task.Delay(60);
        var elapsed = MonotonicClock.ElapsedMs(t0);

        Assert.True(elapsed >= 40, $"expected >= 40ms, got {elapsed}ms");
    }

    [Fact]
    public void Timestamp_IsMonotonicNonDecreasing()
    {
        var a = MonotonicClock.Timestamp;
        var b = MonotonicClock.Timestamp;
        Assert.True(b >= a);
    }
}
