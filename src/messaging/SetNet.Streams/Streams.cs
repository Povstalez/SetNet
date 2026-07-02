using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Streams
{
    /// <summary>Reserved wire types for large-payload streaming. Don't reuse these ids for application messages.</summary>
    public static class StreamTypes
    {
        /// <summary>Offer/accept/reject/complete/cancel control frames (both directions).</summary>
        public const ushort Control = ushort.MaxValue - 42;   // 65493

        /// <summary>One chunk of stream content: <c>[16 streamId][8 offset][chunk]</c>.</summary>
        public const ushort Data = ushort.MaxValue - 43;      // 65492
    }

    internal enum StreamOp : byte { Offer = 0, Accept = 1, Reject = 2, Complete = 3, Cancel = 4 }

    /// <summary>Thrown when a stream transfer fails (rejected, cancelled, timed out, integrity mismatch).</summary>
    public sealed class StreamsException : Exception
    {
        /// <summary>The id of the transfer that failed; pass it back as <c>streamId</c> to resume an interrupted upload.</summary>
        public Guid StreamId { get; }

        /// <summary>Creates the exception with a message and the failing transfer's id.</summary>
        public StreamsException(string message, Guid streamId) : base(message) { StreamId = streamId; }
    }

    /// <summary>Tuning knobs for the streaming layer.</summary>
    public sealed class StreamsOptions
    {
        /// <summary>
        /// When no <c>OfferReceived</c> subscriber decides, offers up to <see cref="MaxAutoAcceptBytes"/> are accepted
        /// automatically into an in-memory sink. Disable to reject all unhandled offers.
        /// </summary>
        public bool AutoAccept { get; set; } = true;

        /// <summary>Size cap for auto-accepted transfers (default 64 MB). Larger offers are rejected unless the app handles them.</summary>
        public long MaxAutoAcceptBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>Chunk size for outgoing transfers (default 64 KB). Keep well under <c>Configuration.MaxMessageSize</c>; use ~1 KB over reliable UDP.</summary>
        public int ChunkSize { get; set; } = 64 * 1024;

        /// <summary>How long the sender waits for the receiver to accept an offer (default 30 s).</summary>
        public int OfferTimeoutMs { get; set; } = 30_000;

        /// <summary>How long an interrupted incoming transfer is kept for resume before being swept (default 10 min).</summary>
        public double PartialTtlSeconds { get; set; } = 600;
    }

    // ---- sinks ----

    /// <summary>Where an incoming stream's bytes go. Chunks arrive sequentially (offset strictly increasing).</summary>
    public interface IStreamSink
    {
        /// <summary>Writes one chunk at the given absolute offset.</summary>
        Task WriteAsync(long offset, byte[] chunk, int count);

        /// <summary>Called once after the final chunk when the transfer completed successfully.</summary>
        Task CompleteAsync();
    }

    /// <summary>Buffers the whole transfer in memory. The default sink for auto-accepted offers.</summary>
    public sealed class MemoryStreamSink : IStreamSink
    {
        private readonly MemoryStream _buffer = new MemoryStream();

        /// <inheritdoc/>
        public Task WriteAsync(long offset, byte[] chunk, int count)
        {
            lock (_buffer)
            {
                _buffer.Position = offset;
                _buffer.Write(chunk, 0, count);
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task CompleteAsync() => Task.CompletedTask;

        /// <summary>The received bytes.</summary>
        public byte[] ToArray() { lock (_buffer) return _buffer.ToArray(); }
    }

    /// <summary>Writes the transfer straight to a file — resume-friendly (a partial file keeps its bytes across attempts).</summary>
    public sealed class FileStreamSink : IStreamSink, IDisposable
    {
        private readonly FileStream _file;

        /// <summary>Opens (or creates) <paramref name="path"/> for writing.</summary>
        public FileStreamSink(string path)
            => _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

        /// <inheritdoc/>
        public async Task WriteAsync(long offset, byte[] chunk, int count)
        {
            _file.Position = offset;
            await _file.WriteAsync(chunk, 0, count).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task CompleteAsync() { await _file.FlushAsync().ConfigureAwait(false); }

        /// <summary>Closes the underlying file.</summary>
        public void Dispose() => _file.Dispose();
    }

    // ---- wire ----

    internal sealed class StreamControl
    {
        public Guid StreamId;
        public StreamOp Op;
        public string Name = "";
        public long TotalLength;
        public int ChunkSize;
        public long Offset;      // Accept: resume offset the sender must start from
        public string Error = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(StreamId.ToByteArray());
            w.Write((byte)Op);
            w.Write(Name ?? "");
            w.Write(TotalLength);
            w.Write(ChunkSize);
            w.Write(Offset);
            w.Write(Error ?? "");
            return ms.ToArray();
        }

        public static StreamControl Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new StreamControl
            {
                StreamId = new Guid(r.ReadBytes(16)),
                Op = (StreamOp)r.ReadByte(),
                Name = r.ReadString(),
                TotalLength = r.ReadInt64(),
                ChunkSize = r.ReadInt32(),
                Offset = r.ReadInt64(),
                Error = r.ReadString(),
            };
        }
    }

    internal static class StreamDataCodec
    {
        public const int HeaderSize = 24;

        public static byte[] Encode(Guid streamId, long offset, byte[] chunk, int count)
        {
            var frame = new byte[HeaderSize + count];
            streamId.ToByteArray().CopyTo(frame, 0);
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(16, 8), offset);
            Buffer.BlockCopy(chunk, 0, frame, HeaderSize, count);
            return frame;
        }

        public static (Guid StreamId, long Offset, byte[] Chunk)? Decode(byte[] frame)
        {
            if (frame == null || frame.Length < HeaderSize) return null;
            var idBytes = new byte[16];
            Buffer.BlockCopy(frame, 0, idBytes, 0, 16);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(16, 8));
            var chunk = new byte[frame.Length - HeaderSize];
            Buffer.BlockCopy(frame, HeaderSize, chunk, 0, chunk.Length);
            return (new Guid(idBytes), offset, chunk);
        }
    }

    // ---- public surface objects ----

    /// <summary>A completed incoming transfer, handed to the <c>Received</c> event.</summary>
    public sealed class CompletedStream
    {
        /// <summary>The transfer's id.</summary>
        public Guid StreamId { get; internal set; }

        /// <summary>The sender-supplied display name (file name, asset key…).</summary>
        public string Name { get; internal set; } = "";

        /// <summary>Total content length in bytes.</summary>
        public long Length { get; internal set; }

        /// <summary>The sink the bytes were written into (cast to <see cref="MemoryStreamSink"/> for auto-accepted transfers).</summary>
        public IStreamSink Sink { get; internal set; } = null!;
    }

    /// <summary>
    /// An incoming offer awaiting a decision. Call <see cref="AcceptAsync"/> (optionally with a custom sink, e.g. a
    /// <see cref="FileStreamSink"/>) or <see cref="RejectAsync"/>. Undecided offers fall back to the auto-accept policy.
    /// </summary>
    public sealed class IncomingStreamOffer
    {
        private readonly StreamEndpoint _endpoint;
        private int _decided;

        /// <summary>The transfer's id (matches the sender's <c>streamId</c>).</summary>
        public Guid StreamId { get; }

        /// <summary>The sender-supplied display name.</summary>
        public string Name { get; }

        /// <summary>Announced total length in bytes.</summary>
        public long Length { get; }

        internal IncomingStreamOffer(StreamEndpoint endpoint, Guid id, string name, long length)
        {
            _endpoint = endpoint;
            StreamId = id;
            Name = name;
            Length = length;
        }

        internal bool TryClaimDecision() => Interlocked.CompareExchange(ref _decided, 1, 0) == 0;

        /// <summary>Accepts the transfer into <paramref name="sink"/> (an in-memory sink when null).</summary>
        public Task AcceptAsync(IStreamSink? sink = null)
        {
            if (!TryClaimDecision()) return Task.CompletedTask;
            return _endpoint.AcceptOffer(this, sink ?? new MemoryStreamSink());
        }

        /// <summary>Rejects the transfer; the sender's <c>SendAsync</c> throws with <paramref name="reason"/>.</summary>
        public Task RejectAsync(string reason = "Rejected by receiver.")
        {
            if (!TryClaimDecision()) return Task.CompletedTask;
            return _endpoint.RejectOffer(this, reason);
        }
    }

    // ---- the direction-agnostic engine ----

    /// <summary>Sender-side bookkeeping for one in-flight outgoing transfer, completed by the receiver's control replies.</summary>
    internal sealed class PendingSend
    {
        public readonly TaskCompletionSource<StreamControl> Accepted = new TaskCompletionSource<StreamControl>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<StreamControl> Finished = new TaskCompletionSource<StreamControl>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Global sender-side registry: stream ids are process-unique GUIDs, so one map serves clients and servers alike.</summary>
    internal static class PendingSends
    {
        private static readonly ConcurrentDictionary<Guid, PendingSend> Map = new ConcurrentDictionary<Guid, PendingSend>();

        public static PendingSend Create(Guid id) { var p = new PendingSend(); Map[id] = p; return p; }
        public static void Remove(Guid id) => Map.TryRemove(id, out _);

        public static void OnControl(StreamControl ctrl)
        {
            if (!Map.TryGetValue(ctrl.StreamId, out var pending)) return;
            switch (ctrl.Op)
            {
                case StreamOp.Accept: pending.Accepted.TrySetResult(ctrl); break;
                case StreamOp.Reject: pending.Accepted.TrySetException(new StreamsException(ctrl.Error, ctrl.StreamId)); break;
                case StreamOp.Complete: pending.Finished.TrySetResult(ctrl); break;
                case StreamOp.Cancel:
                    var ex = new StreamsException(ctrl.Error, ctrl.StreamId);
                    pending.Accepted.TrySetException(ex);
                    pending.Finished.TrySetException(ex);
                    break;
            }
        }
    }

    /// <summary>
    /// Receiver-side state machine for one endpoint (a client, or one server peer): tracks announced transfers,
    /// writes sequential chunks into their sinks, keeps interrupted partials for resume, and answers the sender's
    /// control frames. Chunks are validated to be exactly contiguous — anything out of order cancels the transfer
    /// (the layer requires a reliable, ordered delivery path).
    /// </summary>
    internal sealed class StreamEndpoint
    {
        internal sealed class Incoming
        {
            public string Name = "";
            public long TotalLength;
            public IStreamSink Sink = null!;
            public long Received;            // contiguous bytes written so far == the resume offset
            public bool Active;              // false once interrupted; kept for resume until TTL
            public long TouchedTicks;
        }

        private readonly ConcurrentDictionary<Guid, Incoming> _incoming = new ConcurrentDictionary<Guid, Incoming>();
        private readonly Func<ushort, byte[], Task> _send;
        private readonly StreamsOptions _options;
        private readonly Action<IncomingStreamOffer> _raiseOffer;
        private readonly Action<CompletedStream> _raiseReceived;
        private readonly Func<bool> _hasOfferSubscribers;

        public StreamEndpoint(
            Func<ushort, byte[], Task> send,
            StreamsOptions options,
            Action<IncomingStreamOffer> raiseOffer,
            Action<CompletedStream> raiseReceived,
            Func<bool> hasOfferSubscribers)
        {
            _send = send;
            _options = options;
            _raiseOffer = raiseOffer;
            _raiseReceived = raiseReceived;
            _hasOfferSubscribers = hasOfferSubscribers;
        }

        public async Task OnControl(StreamControl ctrl)
        {
            switch (ctrl.Op)
            {
                case StreamOp.Offer:
                    Sweep();
                    var offer = new IncomingStreamOffer(this, ctrl.StreamId, ctrl.Name, ctrl.TotalLength);
                    if (_hasOfferSubscribers())
                    {
                        _raiseOffer(offer);   // the app decides via offer.AcceptAsync/RejectAsync
                    }
                    else if (_options.AutoAccept && ctrl.TotalLength <= _options.MaxAutoAcceptBytes)
                    {
                        await offer.AcceptAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await offer.RejectAsync("No receiver accepted the stream.").ConfigureAwait(false);
                    }
                    break;

                case StreamOp.Complete:   // sender says it sent everything
                    if (_incoming.TryGetValue(ctrl.StreamId, out var done))
                    {
                        if (done.Received == done.TotalLength)
                        {
                            _incoming.TryRemove(ctrl.StreamId, out _);
                            await done.Sink.CompleteAsync().ConfigureAwait(false);
                            await Reply(ctrl.StreamId, StreamOp.Complete, offset: done.Received).ConfigureAwait(false);
                            _raiseReceived(new CompletedStream { StreamId = ctrl.StreamId, Name = done.Name, Length = done.TotalLength, Sink = done.Sink });
                        }
                        else
                        {
                            _incoming.TryRemove(ctrl.StreamId, out _);
                            await Reply(ctrl.StreamId, StreamOp.Cancel, error: $"Incomplete: got {done.Received} of {done.TotalLength} bytes.").ConfigureAwait(false);
                        }
                    }
                    break;

                case StreamOp.Cancel:     // sender aborted mid-transfer; keep the partial for resume
                    if (_incoming.TryGetValue(ctrl.StreamId, out var cancelled))
                    {
                        cancelled.Active = false;
                        cancelled.TouchedTicks = Stopwatch.GetTimestamp();
                    }
                    break;

                // Accept/Reject are sender-side concerns; they never arrive at the receiving endpoint.
            }
        }

        public async Task OnData(Guid streamId, long offset, byte[] chunk)
        {
            if (!_incoming.TryGetValue(streamId, out var transfer) || !transfer.Active) return;

            // The transport is ordered and the sender is sequential, so anything non-contiguous means corruption
            // or misuse — cancel rather than silently assemble a broken payload.
            if (offset != transfer.Received)
            {
                transfer.Active = false;
                await Reply(streamId, StreamOp.Cancel, error: $"Out-of-order chunk at {offset}, expected {transfer.Received}.").ConfigureAwait(false);
                return;
            }
            if (transfer.Received + chunk.Length > transfer.TotalLength)
            {
                transfer.Active = false;
                await Reply(streamId, StreamOp.Cancel, error: "Chunk overruns the announced length.").ConfigureAwait(false);
                return;
            }

            await transfer.Sink.WriteAsync(offset, chunk, chunk.Length).ConfigureAwait(false);
            transfer.Received += chunk.Length;
            transfer.TouchedTicks = Stopwatch.GetTimestamp();
        }

        internal Task AcceptOffer(IncomingStreamOffer offer, IStreamSink sink)
        {
            // A partial with the same id (an interrupted earlier attempt) resumes where it left off.
            var incoming = _incoming.AddOrUpdate(
                offer.StreamId,
                _ => new Incoming { Name = offer.Name, TotalLength = offer.Length, Sink = sink, Received = 0, Active = true, TouchedTicks = Stopwatch.GetTimestamp() },
                (_, existing) =>
                {
                    existing.Active = true;
                    existing.TouchedTicks = Stopwatch.GetTimestamp();
                    return existing;
                });
            return Reply(offer.StreamId, StreamOp.Accept, offset: incoming.Received);
        }

        internal Task RejectOffer(IncomingStreamOffer offer, string reason)
            => Reply(offer.StreamId, StreamOp.Reject, error: reason);

        private Task Reply(Guid id, StreamOp op, long offset = 0, string error = "")
            => _send(StreamTypes.Control, new StreamControl { StreamId = id, Op = op, Offset = offset, Error = error }.Encode());

        /// <summary>Drops interrupted partials that outlived the resume TTL so abandoned transfers can't leak sinks.</summary>
        private void Sweep()
        {
            var now = Stopwatch.GetTimestamp();
            List<Guid>? drop = null;
            foreach (var kv in _incoming)
                if (!kv.Value.Active && (now - kv.Value.TouchedTicks) / (double)Stopwatch.Frequency > _options.PartialTtlSeconds)
                    (drop ??= new List<Guid>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _incoming.TryRemove(k, out _);
        }
    }

    /// <summary>Shared sender logic: offer → await accept (resume offset) → sequential chunks → complete → await ack.</summary>
    internal static class StreamSender
    {
        public static async Task<Guid> SendAsync(
            Func<ushort, byte[], Task> send,
            StreamsOptions options,
            string name,
            Stream content,
            IProgress<double>? progress,
            Guid? streamId,
            CancellationToken cancellationToken)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (!content.CanSeek) throw new ArgumentException("Content stream must be seekable (length + resume).", nameof(content));

            var id = streamId ?? Guid.NewGuid();
            var total = content.Length;
            var pending = PendingSends.Create(id);
            try
            {
                var offer = new StreamControl { StreamId = id, Op = StreamOp.Offer, Name = name ?? "", TotalLength = total, ChunkSize = options.ChunkSize };
                await send(StreamTypes.Control, offer.Encode()).ConfigureAwait(false);

                var accept = await Await(pending.Accepted.Task, options.OfferTimeoutMs, id, "Offer was not accepted in time.").ConfigureAwait(false);

                var position = Math.Min(Math.Max(accept.Offset, 0), total);   // resume offset dictated by the receiver
                content.Position = position;
                var buffer = new byte[options.ChunkSize];
                while (position < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await content.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, total - position), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) throw new StreamsException("Content stream ended before the announced length.", id);

                    await send(StreamTypes.Data, StreamDataCodec.Encode(id, position, buffer, read)).ConfigureAwait(false);
                    position += read;
                    progress?.Report(total == 0 ? 1 : (double)position / total);

                    // A receiver-side cancel (out-of-order, overrun) surfaces here instead of after a full upload.
                    if (pending.Finished.Task.IsFaulted) await pending.Finished.Task.ConfigureAwait(false);
                }

                await send(StreamTypes.Control, new StreamControl { StreamId = id, Op = StreamOp.Complete }.Encode()).ConfigureAwait(false);
                await Await(pending.Finished.Task, options.OfferTimeoutMs, id, "Receiver did not confirm completion.").ConfigureAwait(false);
                progress?.Report(1);
                return id;
            }
            catch (OperationCanceledException)
            {
                // Tell the receiver to park the partial for resume, then surface the cancellation.
                try { await send(StreamTypes.Control, new StreamControl { StreamId = id, Op = StreamOp.Cancel, Error = "Cancelled by sender." }.Encode()).ConfigureAwait(false); } catch { }
                throw new StreamsException("Transfer cancelled by sender.", id);
            }
            finally { PendingSends.Remove(id); }
        }

        private static async Task<StreamControl> Await(Task<StreamControl> task, int timeoutMs, Guid id, string timeoutMessage)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != task) throw new StreamsException(timeoutMessage, id);
            return await task.ConfigureAwait(false);
        }
    }

    // ---- client side ----

    /// <summary>
    /// Client-side streaming driver, attached by <see cref="StreamsClientExtensions.UseStreams"/>. Push large payloads
    /// (patches, avatars, replays) to the server with progress + resume, and receive server-pushed streams via
    /// <see cref="OfferReceived"/>/<see cref="Received"/>.
    /// </summary>
    public sealed class StreamsClient
    {
        private static readonly ConcurrentDictionary<StreamsClient, byte> Instances = new ConcurrentDictionary<StreamsClient, byte>();

        private readonly BaseClient _client;
        private readonly StreamsOptions _options;
        internal readonly StreamEndpoint Endpoint;

        /// <summary>Raised per incoming offer; call <c>AcceptAsync</c>/<c>RejectAsync</c> on it (else the auto-accept policy applies).</summary>
        public event Action<IncomingStreamOffer>? OfferReceived;

        /// <summary>Raised when an incoming transfer finished; the payload sits in <see cref="CompletedStream.Sink"/>.</summary>
        public event Action<CompletedStream>? Received;

        internal StreamsClient(BaseClient client, StreamsOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options;
            Endpoint = new StreamEndpoint(
                (type, payload) => _client.SendAsync(type, payload, DeliveryMethod.Reliable),
                options,
                offer => OfferReceived?.Invoke(offer),
                completed => Received?.Invoke(completed),
                () => OfferReceived != null);
            Instances[this] = 0;
        }

        /// <summary>
        /// Streams <paramref name="content"/> to the server. Completes when the receiver confirmed the full payload;
        /// returns the transfer id. To resume an interrupted upload, call again with the same <paramref name="streamId"/>
        /// (taken from the thrown <see cref="StreamsException.StreamId"/>) — only the missing tail is re-sent.
        /// </summary>
        public Task<Guid> SendAsync(string name, Stream content, IProgress<double>? progress = null, Guid? streamId = null, CancellationToken cancellationToken = default)
            => StreamSender.SendAsync((type, payload) => _client.SendAsync(type, payload, DeliveryMethod.Reliable), _options, name, content, progress, streamId, cancellationToken);

        /// <summary>Convenience overload streaming an in-memory buffer.</summary>
        public Task<Guid> SendAsync(string name, byte[] content, IProgress<double>? progress = null, Guid? streamId = null, CancellationToken cancellationToken = default)
            => SendAsync(name, new MemoryStream(content ?? Array.Empty<byte>()), progress, streamId, cancellationToken);

        internal static Task DispatchControl(StreamControl ctrl)
        {
            PendingSends.OnControl(ctrl);   // completes this process's outgoing sends
            var tasks = new List<Task>();
            foreach (var c in Instances.Keys) tasks.Add(c.Endpoint.OnControl(ctrl));
            return Task.WhenAll(tasks);
        }

        internal static Task DispatchData(Guid id, long offset, byte[] chunk)
        {
            var tasks = new List<Task>();
            foreach (var c in Instances.Keys) tasks.Add(c.Endpoint.OnData(id, offset, chunk));
            return Task.WhenAll(tasks);
        }
    }

    // ---- server side ----

    /// <summary>
    /// Server-side streaming hub, attached by <see cref="StreamsServerExtensions.UseStreams"/>. Receives client
    /// uploads (per-peer offers/completions) and pushes streams down to individual peers.
    /// </summary>
    public sealed class StreamsServer
    {
        private static readonly ConcurrentDictionary<BaseServer, StreamsServer> Servers = new ConcurrentDictionary<BaseServer, StreamsServer>();
        private static readonly ConditionalWeakTable<BasePeer, StreamEndpoint> PeerEndpoints = new ConditionalWeakTable<BasePeer, StreamEndpoint>();

        private readonly StreamsOptions _options;

        /// <summary>Raised per incoming offer from a peer; call <c>AcceptAsync</c>/<c>RejectAsync</c> (else the auto-accept policy applies).</summary>
        public event Action<BasePeer, IncomingStreamOffer>? OfferReceived;

        /// <summary>Raised when a peer's transfer finished; the payload sits in <see cref="CompletedStream.Sink"/>.</summary>
        public event Action<BasePeer, CompletedStream>? Received;

        internal StreamsServer(StreamsOptions options) { _options = options; }

        internal static StreamsServer Enable(BaseServer server, StreamsOptions options)
            => Servers.GetOrAdd(server, _ => new StreamsServer(options));

        /// <summary>
        /// Streams <paramref name="content"/> down to one peer. Completes when the peer confirmed the full payload;
        /// returns the transfer id (reusable as <paramref name="streamId"/> to resume).
        /// </summary>
        public Task<Guid> SendAsync(BasePeer peer, string name, Stream content, IProgress<double>? progress = null, Guid? streamId = null, CancellationToken cancellationToken = default)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            return StreamSender.SendAsync((type, payload) => peer.SendAsync(type, payload, DeliveryMethod.Reliable), _options, name, content, progress, streamId, cancellationToken);
        }

        /// <summary>Convenience overload streaming an in-memory buffer to one peer.</summary>
        public Task<Guid> SendAsync(BasePeer peer, string name, byte[] content, IProgress<double>? progress = null, Guid? streamId = null, CancellationToken cancellationToken = default)
            => SendAsync(peer, name, new MemoryStream(content ?? Array.Empty<byte>()), progress, streamId, cancellationToken);

        internal static StreamEndpoint? EndpointFor(BasePeer peer)
        {
            var server = peer.CurrentPeerInfo.Server;
            if (server == null || !Servers.TryGetValue(server, out var hub)) return null;
            return PeerEndpoints.GetValue(peer, p => new StreamEndpoint(
                (type, payload) => p.SendAsync(type, payload, DeliveryMethod.Reliable),
                hub._options,
                offer => hub.OfferReceived?.Invoke(p, offer),
                completed => hub.Received?.Invoke(p, completed),
                () => hub.OfferReceived != null));
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for stream control frames (offers from peers + replies to server-pushed streams).</summary>
    [MessageHandler(StreamTypes.Control)]
    public sealed class StreamControlServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var ctrl = StreamControl.Decode(data);
            PendingSends.OnControl(ctrl);   // completes server → peer sends
            return StreamsServer.EndpointFor(peer)?.OnControl(ctrl) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered server handler for stream data chunks.</summary>
    [MessageHandler(StreamTypes.Data)]
    public sealed class StreamDataServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var decoded = StreamDataCodec.Decode(data);
            if (decoded == null) return Task.CompletedTask;
            return StreamsServer.EndpointFor(peer)?.OnData(decoded.Value.StreamId, decoded.Value.Offset, decoded.Value.Chunk) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for stream control frames.</summary>
    [MessageHandler(StreamTypes.Control)]
    public sealed class StreamControlClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) => StreamsClient.DispatchControl(StreamControl.Decode(data));
    }

    /// <summary>Auto-discovered client handler for stream data chunks.</summary>
    [MessageHandler(StreamTypes.Data)]
    public sealed class StreamDataClientHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var decoded = StreamDataCodec.Decode(data);
            if (decoded == null) return Task.CompletedTask;
            return StreamsClient.DispatchData(decoded.Value.StreamId, decoded.Value.Offset, decoded.Value.Chunk);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the streaming hub to a server by composition.</summary>
    public static class StreamsServerExtensions
    {
        /// <summary>Enables server-side streaming; returns the hub (per-peer offers/completions + SendAsync to peers).</summary>
        public static StreamsServer UseStreams(this BaseServer server, StreamsOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return StreamsServer.Enable(server, options ?? new StreamsOptions());
        }
    }

    /// <summary>Attaches a streaming driver to a client by composition.</summary>
    public static class StreamsClientExtensions
    {
        /// <summary>Enables client-side streaming; returns the driver (SendAsync + offer/received events).</summary>
        public static StreamsClient UseStreams(this BaseClient client, StreamsOptions? options = null)
            => new StreamsClient(client, options ?? new StreamsOptions());
    }

    /// <summary>One-time bootstrap so the streaming handlers are discovered. Call at startup.</summary>
    public static class StreamsRuntime
    {
        /// <summary>Ensures the streaming layer is discoverable.</summary>
        public static void Enable() { _ = StreamTypes.Control; }
    }
}
