using System.Collections.Generic;
using SetNet.StateSync.Prediction;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the client-side prediction buffer (pure logic, no networking).</summary>
public class PredictionBufferTests
{
    [Fact]
    public void Reconcile_Drops_Acked_And_Replays_Rest_In_Order()
    {
        var buffer = new PredictionBuffer<int>();
        buffer.Record(1, 10);
        buffer.Record(2, 20);
        buffer.Record(3, 30);
        Assert.Equal(3, buffer.PendingCount);

        var replayed = new List<int>();
        buffer.Reconcile(lastProcessedInput: 1, apply: replayed.Add);

        Assert.Equal(new[] { 20, 30 }, replayed);   // input 10 (seq 1) acknowledged → dropped
        Assert.Equal(2, buffer.PendingCount);
    }

    [Fact]
    public void Reconcile_All_Acked_Leaves_Nothing()
    {
        var buffer = new PredictionBuffer<int>();
        buffer.Record(1, 10);
        buffer.Record(2, 20);

        var replayed = new List<int>();
        buffer.Reconcile(5, replayed.Add);

        Assert.Empty(replayed);
        Assert.Equal(0, buffer.PendingCount);
    }
}
