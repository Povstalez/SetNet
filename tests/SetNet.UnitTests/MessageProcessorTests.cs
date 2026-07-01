using System;
using System.Threading.Tasks;
using SetNet.Messaging;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for type-based dispatch and error surfacing in <see cref="MessageProcessor"/>.</summary>
public class MessageProcessorTests
{
    [Fact]
    public void SyncHandler_IsInvokedWithPayload()
    {
        var mp = new MessageProcessor();
        byte[]? got = null;
        mp.RegisterHandler((ushort)5, data => { got = data; });

        mp.ProcessMessage(5, new byte[] { 1, 2 });

        Assert.Equal(new byte[] { 1, 2 }, got);
    }

    [Fact]
    public async Task AsyncHandler_IsInvokedWithPayload()
    {
        var mp = new MessageProcessor();
        var tcs = new TaskCompletionSource<byte[]>();
        mp.RegisterHandler((ushort)5, async data => { await Task.Yield(); tcs.TrySetResult(data); });

        mp.ProcessMessage(5, new byte[] { 9 });

        Assert.Equal(new byte[] { 9 }, await tcs.Task);
    }

    [Fact]
    public async Task AsyncHandlerException_IsReportedViaOnHandlerError()
    {
        var mp = new MessageProcessor();
        var errTcs = new TaskCompletionSource<Exception>();
        mp.OnHandlerError = (type, ex) => errTcs.TrySetResult(ex);
        mp.RegisterHandler((ushort)5, async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); });

        mp.ProcessMessage(5, Array.Empty<byte>());

        Assert.IsType<InvalidOperationException>(await errTcs.Task);
    }

    [Fact]
    public void SyncHandlerException_IsReportedViaOnHandlerError()
    {
        var mp = new MessageProcessor();
        Exception? reported = null;
        mp.OnHandlerError = (type, ex) => reported = ex;
        mp.RegisterHandler((ushort)5, _ => throw new InvalidOperationException("sync boom"));

        mp.ProcessMessage(5, Array.Empty<byte>());

        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void UnknownType_IsDroppedWithoutThrowing()
    {
        var mp = new MessageProcessor();
        mp.ProcessMessage(999, Array.Empty<byte>()); // no handler registered → no-op
    }

    [Fact]
    public void ThrowingErrorSink_OnSyncHandlerFault_DoesNotEscape()
    {
        // Regression: a faulty OnHandlerError (e.g. a logger that throws) must not escape ProcessMessage —
        // for the async path it would otherwise crash the process via async void.
        var mp = new MessageProcessor();
        mp.OnHandlerError = (_, _) => throw new InvalidOperationException("logger boom");
        mp.RegisterHandler((ushort)5, _ => throw new InvalidOperationException("handler boom"));

        mp.ProcessMessage(5, Array.Empty<byte>()); // must not throw
    }

    [Fact]
    public async Task ThrowingErrorSink_OnAsyncHandlerFault_DoesNotCrash()
    {
        // The async observer is `async void`; if its OnHandlerError throws, the exception would become an
        // unhandled exception on a thread-pool thread and terminate the process. ReportError must swallow it.
        var mp = new MessageProcessor();
        var entered = new TaskCompletionSource<bool>();
        mp.OnHandlerError = (_, _) => { entered.TrySetResult(true); throw new InvalidOperationException("logger boom"); };
        mp.RegisterHandler((ushort)5, async _ => { await Task.Yield(); throw new InvalidOperationException("handler boom"); });

        mp.ProcessMessage(5, Array.Empty<byte>());

        Assert.True(await entered.Task); // the sink ran and threw; we are still alive
    }
}
