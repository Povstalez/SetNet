using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SetNet.Protocol
{
    /// <summary>
    /// Process-wide registry of in-flight unified-protocol requests, keyed by a process-unique correlation id.
    /// Because the id is unique across the whole process, the (connection-less) client envelope handler can
    /// complete the right awaiting call without knowing which client it belongs to — this is what lets every module
    /// share one request/reply mechanism instead of each maintaining its own.
    /// <para>
    /// Ids are drawn from a cryptographic RNG rather than a monotonic counter, so a peer (e.g. a malicious server a
    /// client is talking to) cannot steer an unsolicited reply onto another in-flight call by predicting the next id.
    /// </para>
    /// </summary>
    internal static class ProtocolCorrelation
    {
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<ProtocolEnvelope>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<ProtocolEnvelope>>();

        /// <summary>
        /// Reserves an unpredictable, process-unique correlation id and registers <paramref name="tcs"/> under it in
        /// one atomic step. Returns the id to stamp on the outgoing request. The (astronomically rare) RNG collision
        /// with a still-pending id is retried, so the returned id is always free.
        /// </summary>
        public static int Reserve(TaskCompletionSource<ProtocolEnvelope> tcs)
        {
            while (true)
            {
                var id = NextRandomNonZero();
                if (Pending.TryAdd(id, tcs)) return id;
            }
        }

        /// <summary>Removes a pending call (on completion, timeout, or cancellation).</summary>
        public static void Remove(int correlationId) => Pending.TryRemove(correlationId, out _);

        /// <summary>Draws a non-zero 32-bit correlation id from the crypto RNG (0 is reserved for "no correlation").</summary>
        private static int NextRandomNonZero()
        {
            Span<byte> b = stackalloc byte[4];
            int v;
            do { RandomNumberGenerator.Fill(b); v = BitConverter.ToInt32(b); } while (v == 0);
            return v;
        }

        /// <summary>Completes the awaiting call for <paramref name="correlationId"/> with its reply/error, if still pending.</summary>
        public static void Complete(int correlationId, ProtocolEnvelope reply)
        {
            if (Pending.TryGetValue(correlationId, out var tcs))
                tcs.TrySetResult(reply);
        }
    }
}
