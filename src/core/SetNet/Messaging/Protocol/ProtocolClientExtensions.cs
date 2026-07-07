using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Protocol
{
    /// <summary>
    /// The uniform client-side sending surface for the unified protocol, added to <see cref="BaseClient"/> by
    /// extension so it sits alongside the regular <c>SendAsync</c>. Every module uses these same three verbs —
    /// correlated <c>RequestAsync</c>, fire-and-forget <c>PostAsync</c>, and event subscription <c>On</c> — instead
    /// of hand-rolling correlation registries and event fan-out. Raw (<c>byte[]</c>) variants keep module control
    /// messages serializer-agnostic; the generic overloads add typed convenience for the app's own serializable types.
    /// </summary>
    public static class ProtocolClientExtensions
    {
        /// <summary>Default per-request timeout in milliseconds when a caller does not specify one.</summary>
        public const int DefaultTimeoutMs = 10000;

        /// <summary>
        /// Sends a correlated request with a raw (already-framed) body and awaits the raw reply body. Serializer-agnostic.
        /// </summary>
        /// <param name="client">The connected client.</param>
        /// <param name="channel">The target channel id (see <see cref="Channels"/>).</param>
        /// <param name="op">The operation id within the channel.</param>
        /// <param name="body">The request body (may be empty).</param>
        /// <param name="timeoutMs">Per-call timeout in ms; 0 or less waits indefinitely.</param>
        /// <param name="ct">Cancels the wait.</param>
        /// <returns>The reply body.</returns>
        /// <exception cref="ProtocolException">The server-side handler threw, or the channel is not configured.</exception>
        /// <exception cref="TimeoutException">No reply arrived within <paramref name="timeoutMs"/>.</exception>
        public static async Task<byte[]> RequestRawAsync(this BaseClient client, ushort channel, ushort op,
            byte[]? body = null, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var corr = ProtocolCorrelation.NextId();
            var tcs = new TaskCompletionSource<ProtocolEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            ProtocolCorrelation.Register(corr, tcs);
            try
            {
                var env = new ProtocolEnvelope(ProtocolKind.Request, channel, op, corr, body);
                await client.SendAsync(ProtocolTypes.Envelope, env.Encode(), DeliveryMethod.Reliable).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);

                ProtocolEnvelope reply;
                if (timeoutMs <= 0 && !ct.CanBeCanceled)
                {
                    reply = await tcs.Task.ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);
                }
                else
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (timeoutMs > 0) linked.CancelAfter(timeoutMs);
                    using (linked.Token.Register(() => tcs.TrySetCanceled()))
                    {
                        try { reply = await tcs.Task.ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException($"Protocol request (channel {channel}, op {op}) timed out after {timeoutMs} ms.");
                        }
                    }
                }

                if (reply.Kind == ProtocolKind.Error)
                    throw new ProtocolException(Encoding.UTF8.GetString(reply.Body ?? Array.Empty<byte>()));

                return reply.Body ?? Array.Empty<byte>();
            }
            finally
            {
                ProtocolCorrelation.Remove(corr);
            }
        }

        /// <summary>Typed request: serializes <typeparamref name="TReq"/>, awaits, and deserializes <typeparamref name="TResp"/> — for app types the serializer can handle.</summary>
        public static async Task<TResp> RequestAsync<TReq, TResp>(this BaseClient client, ushort channel, ushort op,
            TReq request, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
        {
            var replyBody = await client.RequestRawAsync(channel, op, client.Runtime.Serialize(request), timeoutMs, ct).ConfigureAwait(global::SetNet.SetNetSync.ContinueOnCapturedContext);
            return client.Runtime.Deserialize<TResp>(replyBody);
        }

        /// <summary>Sends a fire-and-forget message with a raw body (no reply expected). Serializer-agnostic.</summary>
        public static Task PostRawAsync(this BaseClient client, ushort channel, ushort op, byte[]? body = null,
            DeliveryMethod delivery = DeliveryMethod.Reliable)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var env = new ProtocolEnvelope(ProtocolKind.Send, channel, op, 0, body);
            return client.SendAsync(ProtocolTypes.Envelope, env.Encode(), delivery);
        }

        /// <summary>Typed fire-and-forget: serializes <typeparamref name="T"/> and sends it (no reply expected).</summary>
        public static Task PostAsync<T>(this BaseClient client, ushort channel, ushort op, T message,
            DeliveryMethod delivery = DeliveryMethod.Reliable)
            => client.PostRawAsync(channel, op, client.Runtime.Serialize(message), delivery);

        /// <summary>Subscribes to raw push events for (channel, op). Returns a handle that unsubscribes on dispose.</summary>
        public static IDisposable OnRaw(this BaseClient client, ushort channel, ushort op, Action<byte[]> handler)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return client.Runtime.ProtocolSubscriptions.Add(channel, op, handler);
        }

        /// <summary>Subscribes to typed push events for (channel, op): each event body is deserialized to <typeparamref name="T"/>.</summary>
        public static IDisposable On<T>(this BaseClient client, ushort channel, ushort op, Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return client.OnRaw(channel, op, body => handler(client.Runtime.Deserialize<T>(body)));
    }
}
}
