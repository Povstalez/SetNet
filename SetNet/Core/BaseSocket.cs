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
        /// Enables back-pressure by bounding concurrent handler execution to <paramref name="maxInFlight"/>.
        /// Called by subclasses from their configuration; 0 or less leaves dispatch unbounded.
        /// </summary>
        /// <param name="maxInFlight">Maximum concurrent handlers per connection; 0 disables the gate.</param>
        protected void InitDispatchGate(int maxInFlight)
        {
            if (maxInFlight > 0)
                _dispatchGate = new SemaphoreSlim(maxInFlight, maxInFlight);
        }

        /// <summary>
        /// Dispatches an inbound message, applying back-pressure when a dispatch gate is configured: it waits
        /// for a free slot before admitting the message (so the caller's receive loop pauses when saturated),
        /// then runs the handler and releases the slot on completion. With no gate it dispatches immediately.
        /// </summary>
        /// <param name="type">The wire type id used to select the handler.</param>
        /// <param name="data">The message payload bytes passed to the handler.</param>
        /// <returns>A task that completes once the message has been admitted for handling (not when the handler finishes).</returns>
        protected async Task DispatchAsync(ushort type, byte[] data)
        {
            var gate = _dispatchGate;
            if (gate == null)
            {
                _messageProcessor.ProcessMessage(type, data);
                return;
            }

            await gate.WaitAsync().ConfigureAwait(false);
            Task handlerTask;
            try { handlerTask = _messageProcessor.ProcessMessageAsync(type, data); }
            catch { gate.Release(); throw; }
            _ = handlerTask.ContinueWith(_ => gate.Release(), TaskScheduler.Default);
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
