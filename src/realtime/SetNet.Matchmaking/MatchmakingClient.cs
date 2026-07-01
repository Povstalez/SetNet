using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Rooms;

namespace SetNet.Matchmaking
{
    /// <summary>
    /// Client-side matchmaking driver, attached by <see cref="MatchmakingClientExtensions.UseMatchmaking"/>. Enter a
    /// queue and await a match; when the server pairs you up you get a <see cref="MatchResult"/> with a room code to
    /// join via your <c>RoomsClient</c>. All by composition, alongside your regular messages.
    /// </summary>
    public sealed class MatchmakingClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private string _ownId = "";
        private string? _waitingQueue;
        private TaskCompletionSource<MatchResult>? _matchTcs;

        /// <summary>Raised when a match is found for this client (also completes the <see cref="FindMatchAsync"/> task).</summary>
        public event Action<MatchResult>? MatchFound;

        internal MatchmakingClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            MatchmakingRegistry.RegisterClient(this);
        }

        /// <summary>True while this client is waiting in a queue.</summary>
        public bool IsSearching { get { lock (_gate) return _waitingQueue != null; } }

        /// <summary>
        /// Enters the queue and waits until the server forms a match. Cancel the token (or call <see cref="CancelAsync"/>)
        /// to leave the queue. Throws <see cref="MatchmakingException"/> if the server rejects the enqueue.
        /// </summary>
        public async Task<MatchResult> FindMatchAsync(MatchRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var tcs = new TaskCompletionSource<MatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_waitingQueue != null) throw new MatchmakingException("Already searching for a match.");
                _matchTcs = tcs;
                _waitingQueue = request.Queue;
            }

            try
            {
                var reply = await SendCommandAsync(MatchOp.Enqueue, request.Queue, request.Skill).ConfigureAwait(false);
                lock (_gate) _ownId = reply.OwnPlayerId;
            }
            catch
            {
                lock (_gate) { _waitingQueue = null; _matchTcs = null; }
                throw;
            }

            using (ct.Register(() => { _ = SafeCancelAsync(); tcs.TrySetCanceled(); }))
            {
                try { return await tcs.Task.ConfigureAwait(false); }
                finally { lock (_gate) { _waitingQueue = null; _matchTcs = null; } }
            }
        }

        /// <summary>
        /// Convenience: find a match, then join the resulting room via <paramref name="rooms"/>. Returns the joined room.
        /// </summary>
        public async Task<RoomInfo> FindAndJoinAsync(MatchRequest request, RoomsClient rooms, CancellationToken ct = default)
        {
            if (rooms == null) throw new ArgumentNullException(nameof(rooms));
            var match = await FindMatchAsync(request, ct).ConfigureAwait(false);
            return await rooms.JoinAsync(match.RoomCode).ConfigureAwait(false);
        }

        /// <summary>Leaves the queue if currently searching.</summary>
        public async Task CancelAsync()
        {
            bool searching;
            lock (_gate) searching = _waitingQueue != null;
            if (!searching) return;
            await SendCommandAsync(MatchOp.Cancel, "", 0).ConfigureAwait(false);
            lock (_gate) { _waitingQueue = null; _matchTcs?.TrySetCanceled(); _matchTcs = null; }
        }

        private async Task SafeCancelAsync()
        {
            try { await SendCommandAsync(MatchOp.Cancel, "", 0).ConfigureAwait(false); } catch { /* best effort */ }
        }

        private async Task<MatchReply> SendCommandAsync(MatchOp op, string queue, int skill)
        {
            var correlationId = MatchmakingRegistry.NextId();
            var tcs = new TaskCompletionSource<MatchReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            MatchmakingRegistry.Register(correlationId, tcs);
            try
            {
                var command = new MatchCommand(correlationId, op, queue, skill);
                await _client.SendAsync(MatchTypes.Command, command.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    MatchReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new MatchmakingException("Matchmaking command timed out."); }
                    if (!reply.Success) throw new MatchmakingException(reply.Error);
                    return reply;
                }
            }
            finally { MatchmakingRegistry.Remove(correlationId); }
        }

        internal void OnEvent(MatchEvent evt)
        {
            TaskCompletionSource<MatchResult>? tcs;
            string ownId;
            lock (_gate)
            {
                if (_waitingQueue == null || evt.Queue != _waitingQueue || evt.Recipient != _ownId) return;   // not for me
                tcs = _matchTcs;
                ownId = _ownId;
            }

            var result = new MatchResult(evt.Queue, evt.RoomCode, evt.Players, ownId);
            MatchFound?.Invoke(result);
            tcs?.TrySetResult(result);
        }
    }

    /// <summary>Attaches matchmaking to a <see cref="BaseClient"/> by composition — no base class.</summary>
    public static class MatchmakingClientExtensions
    {
        /// <summary>Enables matchmaking on a client and returns the driver (find/cancel + <c>MatchFound</c> event).</summary>
        public static MatchmakingClient UseMatchmaking(this BaseClient client) => new MatchmakingClient(client);
    }

    /// <summary>Auto-discovered client handler for matchmaking command replies (correlated).</summary>
    [MessageHandler(MatchTypes.Reply)]
    public sealed class MatchmakingReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            var reply = MatchReply.Decode(data);
            MatchmakingRegistry.Complete(reply.CorrelationId, reply);
            return Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for match-found push events; routes to the matching <see cref="MatchmakingClient"/>.</summary>
    [MessageHandler(MatchTypes.Event)]
    public sealed class MatchmakingEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            MatchmakingRegistry.DispatchEvent(MatchEvent.Decode(data));
            return Task.CompletedTask;
        }
    }
}
