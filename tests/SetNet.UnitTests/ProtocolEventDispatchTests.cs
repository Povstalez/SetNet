using System;
using System.Collections.Generic;
using SetNet;
using SetNet.Protocol;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>
/// Client push-event delivery: both subscription shapes see the same body, and the zero-copy path really is
/// zero-copy — the memory a subscriber receives points into the frame it arrived in, rather than into a fresh
/// array made for it.
/// </summary>
public class ProtocolEventDispatchTests
{
    private const ushort Channel = 4242;
    private const ushort Op = 7;

    private static byte[] Frame(ushort channel, ushort op, byte[] body)
    {
        // Header layout mirrors ProtocolEnvelope: [kind][channel][ushort op][int corr], then the body.
        var frame = new byte[9 + body.Length];
        frame[0] = 3;                                   // ProtocolKind.Event
        frame[1] = (byte)(channel & 0xFF);
        frame[2] = (byte)(channel >> 8);
        frame[3] = (byte)(op & 0xFF);
        frame[4] = (byte)(op >> 8);
        Buffer.BlockCopy(body, 0, frame, 9, body.Length);
        return frame;
    }

    [Fact]
    public void Memory_Subscriber_Reads_Body_Out_Of_The_Received_Frame()
    {
        var runtime = new SetNetRuntime();
        var body = new byte[] { 1, 2, 3, 4 };
        var frame = Frame(Channel, Op, body);

        var seen = new List<byte[]>();
        var sawFrameItself = false;

        using (runtime.ProtocolSubscriptions.AddMemory(Channel, Op, mem =>
        {
            seen.Add(mem.ToArray());

            // The whole point of the memory path: this is a window onto `frame`, not a copy of it. If the
            // dispatcher ever goes back to allocating a body per event, this flag stays false and the test fails.
            sawFrameItself = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(mem, out var segment)
                             && ReferenceEquals(segment.Array, frame);
        }))
        {
            ProtocolDispatcher.DispatchClientAsync(runtime, frame);
        }

        Assert.Single(seen);
        Assert.Equal(body, seen[0]);
        Assert.True(sawFrameItself, "event body should be a slice of the received frame, not a copy");
    }

    [Fact]
    public void Array_Subscribers_Still_Get_Their_Own_Array()
    {
        var runtime = new SetNetRuntime();
        var body = new byte[] { 9, 8, 7 };
        var frame = Frame(Channel, Op, body);

        byte[]? got = null;
        using (runtime.ProtocolSubscriptions.Add(Channel, Op, b => got = b))
        {
            ProtocolDispatcher.DispatchClientAsync(runtime, frame);
        }

        Assert.NotNull(got);
        Assert.Equal(body, got);
        Assert.Equal(body.Length, got!.Length);         // exactly the body, not the frame it rode in
    }

    [Fact]
    public void Both_Subscription_Shapes_Receive_The_Same_Event()
    {
        var runtime = new SetNetRuntime();
        var body = new byte[] { 5, 5, 5 };

        byte[]? fromArray = null;
        byte[]? fromMemory = null;

        using (runtime.ProtocolSubscriptions.Add(Channel, Op, b => fromArray = b))
        using (runtime.ProtocolSubscriptions.AddMemory(Channel, Op, m => fromMemory = m.ToArray()))
        {
            ProtocolDispatcher.DispatchClientAsync(runtime, Frame(Channel, Op, body));
        }

        Assert.Equal(body, fromArray);
        Assert.Equal(body, fromMemory);
    }

    [Fact]
    public void Disposing_A_Memory_Subscription_Stops_Delivery()
    {
        var runtime = new SetNetRuntime();
        var hits = 0;

        var sub = runtime.ProtocolSubscriptions.AddMemory(Channel, Op, _ => hits++);
        ProtocolDispatcher.DispatchClientAsync(runtime, Frame(Channel, Op, new byte[] { 1 }));
        sub.Dispose();
        ProtocolDispatcher.DispatchClientAsync(runtime, Frame(Channel, Op, new byte[] { 1 }));

        Assert.Equal(1, hits);
    }
}
