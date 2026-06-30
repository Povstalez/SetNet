using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core.Transport;
using SetNet.Messaging;

namespace SetNet.Core
{
    /// <summary>
    /// Foundation type for every socket-backed endpoint in the framework (clients and server-side peers).
    /// It owns the active <see cref="ITransportConnection"/> and a <see cref="MessageProcessor"/> that
    /// dispatches inbound frames to registered per-type handlers. Subclasses (<see cref="BaseClient"/> and
    /// <see cref="BasePeer"/>) layer connection lifecycle and send logic on top of this shared plumbing.
    /// </summary>
    public class BaseSocket
    {
        /// <summary>The transport channel for this socket (set by subclasses on connect/accept).</summary>
        protected ITransportConnection? Connection;

        /// <summary>
        /// Routes incoming messages, keyed by their wire type id, to the handler delegates registered
        /// through <see cref="RegisterDataHandler(ushort, Func{byte[], Task})"/>. Shared by all endpoints
        /// so the same dispatch mechanism backs both client- and server-side message handling.
        /// </summary>
        private readonly MessageProcessor _messageProcessor;

        /// <summary>
        /// Optional concurrency gate limiting how many handlers run at once for this socket. When set, the
        /// receive loop pauses on it (back-pressure) before reading more. <c>null</c> = unlimited.
        /// </summary>
        private SemaphoreSlim? _dispatchGate;

        /// <summary>
        /// Cancels a parked <see cref="DispatchAsync"/> gate wait when the socket tears down, so a receive loop
        /// blocked on a saturated gate (e.g. all handler slots held by stuck handlers) unblocks promptly on
        /// close instead of hanging forever. Created alongside the gate; null when no gate is configured.
        /// </summary>
        private CancellationTokenSource? _dispatchCts;

        /// <summary>When true, the receive loop awaits each handler to completion before reading the next frame (in-order, non-overlapping dispatch).</summary>
        private bool _sequentialDispatch;

        /// <summary>
        /// Configures dispatch behaviour for this socket: optional back-pressure (bounding concurrent handler
        /// execution to <paramref name="maxInFlight"/>) and optional sequential (in-order) dispatch. Called by
        /// subclasses from their configuration.
        /// </summary>
        /// <param name="maxInFlight">Maximum concurrent handlers per connection; 0 disables the gate.</param>
        /// <param name="sequential">When true, handlers run one-at-a-time in receive order (takes precedence over the gate).</param>
        protected void InitDispatchGate(int maxInFlight, bool sequential = false)
        {
            _sequentialDispatch = sequential;
            if (maxInFlight > 0)
            {
                _dispatchGate = new SemaphoreSlim(maxInFlight, maxInFlight);
                _dispatchCts = new CancellationTokenSource();
            }
        }

        /// <summary>
        /// Cancels any pending gate wait so the receive loop unblocks during teardown. Idempotent and safe to call
        /// even when no gate is configured. Invoked by subclasses from their close/disconnect path.
        /// </summary>
        protected void ShutdownDispatch()
        {
            try { _dispatchCts?.Cancel(); } catch { /* already disposed */ }
        }

        /// <summary>
        /// Re-arms the dispatch gate for a fresh connection generation by installing a new, uncancelled
        /// cancellation source. Must be called before starting a new receive loop after a teardown
        /// (reconnect, or a Disconnect→Connect cycle), otherwise <see cref="ShutdownDispatch"/> would have left
        /// the token permanently cancelled and the next gated dispatch would throw on the first frame.
        /// </summary>
        /// <remarks>
        /// The previous source is left for the GC rather than disposed: it has no timer (no finalizer cost) and is
        /// only swapped here, between connection generations when no <see cref="DispatchAsync"/> is in flight.
        /// </remarks>
        protected void ResetDispatch()
        {
            if (_dispatchGate != null)
                _dispatchCts = new CancellationTokenSource();
        }

        /// <summary>Disposes the dispatch gate and its cancellation source. Called from the owner's dispose path.</summary>
        protected void DisposeDispatch()
        {
            try { _dispatchCts?.Dispose(); } catch { /* ignore */ }
            try { _dispatchGate?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>
        /// Dispatches an inbound message. In sequential mode it awaits the handler to completion (guaranteeing
        /// in-order, non-overlapping execution). Otherwise, with a dispatch gate it applies back-pressure —
        /// waiting for a free slot before admitting the message (cancellable on teardown) — and with no gate it
        /// dispatches immediately without waiting for the handler.
        /// </summary>
        /// <param name="type">The wire type id used to select the handler.</param>
        /// <param name="data">The message payload bytes passed to the handler.</param>
        /// <returns>A task that completes once the message has been admitted for handling (or, in sequential mode, once the handler finishes).</returns>
        protected async Task DispatchAsync(ushort type, byte[] data)
        {
            // Give the raw-frame interceptor first refusal on application frames (system frames are excluded).
            // If it consumes the frame (e.g. a relay forwards the raw bytes), skip typed dispatch entirely —
            // no deserialization happens. Defaults to a no-op pass-through. A throwing hook is isolated exactly
            // like a faulty handler (reported, frame dropped) so it cannot tear down the receive loop.
            if (!SystemMessageTypes.IsSystem(type))
            {
                bool consumed;
                try { consumed = OnRawFrame(type, data); }
                catch (Exception ex) { HandleProcessingError(type, ex); return; }
                if (consumed) return;
            }

            if (_sequentialDispatch)
            {
                // Await completion so the next frame is not read until this handler finishes — strict ordering.
                await _messageProcessor.ProcessMessageAsync(type, data).ConfigureAwait(false);
                return;
            }

            var gate = _dispatchGate;
            if (gate == null)
            {
                _messageProcessor.ProcessMessage(type, data);
                return;
            }

            // Pass the teardown token so a saturated gate (all slots held by stuck handlers) cannot wedge the
            // receive loop forever — closing the socket cancels the wait and lets the loop exit. Capture the
            // current generation's source locally so a concurrent ResetDispatch swap can't be observed mid-call.
            var cts = _dispatchCts;
            await gate.WaitAsync(cts!.Token).ConfigureAwait(false);

            Task handlerTask;
            try { handlerTask = _messageProcessor.ProcessMessageAsync(type, data); }
            catch { gate.Release(); throw; }
            _ = ReleaseWhenDone(handlerTask, gate);
        }

        /// <summary>Awaits a dispatched handler and releases its gate slot, avoiding the closure a <c>ContinueWith</c> would allocate per message.</summary>
        /// <param name="task">The handler task (never faults — faults are reported inside the processor).</param>
        /// <param name="gate">The gate semaphore to release on completion.</param>
        private static async Task ReleaseWhenDone(Task task, SemaphoreSlim gate)
        {
            try { await task.ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        /// <summary>
        /// Initializes the socket and wires the message processor's error callback to
        /// <see cref="HandleProcessingError"/>, ensuring handler exceptions are surfaced through the
        /// subclass-specific logging path rather than escaping the receive loop.
        /// </summary>
        public BaseSocket()
        {
            _messageProcessor = new MessageProcessor { OnHandlerError = HandleProcessingError };
        }

        /// <summary>
        /// Raw inbound-frame interception hook, called for every <b>application</b> frame (reserved system frames
        /// such as heartbeat/bind-token are excluded) <b>before</b> it is dispatched to a typed handler. Override
        /// it to inspect or forward the raw, still-serialized payload <b>without deserializing</b> — for example a
        /// relay/proxy that re-sends frames to other peers via <c>SendRawAsync</c>.
        /// </summary>
        /// <param name="type">The wire type id of the frame.</param>
        /// <param name="data">The raw, still-serialized payload (a fresh per-message array; safe to keep or forward).</param>
        /// <returns>
        /// <see langword="true"/> to mark the frame consumed and <b>skip</b> typed handler dispatch (relay case);
        /// <see langword="false"/> (the default) to let it continue to its registered handler (normal case, or
        /// observe-and-pass-through such as logging/metrics).
        /// </returns>
        /// <remarks>
        /// Runs synchronously on the receive path; do any forwarding fire-and-forget (or batch it) rather than
        /// blocking. The base implementation returns <see langword="false"/>, so normal endpoints pay nothing.
        /// </remarks>
        protected virtual bool OnRawFrame(ushort type, byte[] data) => false;

        /// <summary>Called when a message handler throws. Overridden by client/peer to log via the configured logger.</summary>
        /// <param name="type">The wire type id of the message whose handler threw.</param>
        /// <param name="exception">The exception raised by the failing handler.</param>
        /// <remarks>
        /// The base implementation deliberately swallows the error so a single faulty handler cannot
        /// tear down the receive loop; subclasses override this to log the failure.
        /// </remarks>
        protected virtual void HandleProcessingError(ushort type, Exception exception) { }

        /// <summary>
        /// Registers an asynchronous handler for messages of the given wire type. Used when handling a
        /// message requires awaiting (for example, sending a response or touching async state).
        /// </summary>
        /// <param name="type">The wire type id this handler should be invoked for.</param>
        /// <param name="handler">The asynchronous delegate that processes the message payload.</param>
        protected void RegisterDataHandler(ushort type, Func<byte[], Task> handler)
        {
            _messageProcessor.RegisterHandler(type, handler);
        }

        /// <summary>
        /// Registers a synchronous handler for messages of the given wire type. Convenient for lightweight,
        /// fire-and-forget processing such as recording a heartbeat timestamp.
        /// </summary>
        /// <param name="type">The wire type id this handler should be invoked for.</param>
        /// <param name="handler">The synchronous delegate that processes the message payload.</param>
        protected void RegisterDataHandler(ushort type, Action<byte[]> handler)
        {
            _messageProcessor.RegisterHandler(type, handler);
        }

        /// <summary>
        /// Dispatches a fully framed inbound message to its registered handler. Called by subclass receive
        /// loops once the transport has reassembled a complete message from the wire.
        /// </summary>
        /// <param name="type">The wire type id used to select the handler.</param>
        /// <param name="data">The deserialized message payload bytes passed to the handler.</param>
        protected void HandleMessage(ushort type, byte[] data)
        {
            _messageProcessor.ProcessMessage(type, data);
        }
    }
}
