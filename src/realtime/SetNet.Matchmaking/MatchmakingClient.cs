using System;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;
using SetNet.Rooms;

namespace SetNet.Matchmaking
{
    /// <summary>
    /// Client-side matchmaking driver, attached by <see cref="MatchmakingClientExtensions.UseMatchmaking"/>. Enter a
    /// queue and await a match; when the server pairs you up you get a <see cref="MatchResult"/> with a room code to
    /// join via your <c>RoomsClient</c>. Rides the unified protocol on the <see cref="Channels.Matchmaking"/> channel.
    /// </summary>
    public sealed class MatchmakingClient
    {
        private readonly BaseClient _client;
        private readonly object _gate = new object();
        private readonly IDisposable _subscription;
        private string _ownId = "";
        private string? _waitingQueue;
        private TaskCompletionSource<MatchResult>? _matchTcs;

        /// <summary>Raised when a match is found for this client (also completes the <see cref="FindMatchAsync"/> task).</summary>
        public event Action<MatchResult>? MatchFound;

        internal MatchmakingClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscription = _client.OnRaw(Channels.Matchmaking, (ushort)MatchEvt.MatchFound, body =>
            {
                var (recipient, queue, roomCode, players) = MatchWire.DecodeMatch(body);
                OnEvent(recipient, queue, roomCode, players);
            });
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
                var ownId = await SendCommandAsync(MatchOp.Enqueue, MatchWire.EncodeEnqueue(request.Queue, request.Skill)).ConfigureAwait(false);
                lock (_gate) _ownId = ownId;
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
            await SendCommandAsync(MatchOp.Cancel, Array.Empty<byte>()).ConfigureAwait(false);
            lock (_gate) { _waitingQueue = null; _matchTcs?.TrySetCanceled(); _matchTcs = null; }
        }

        private async Task SafeCancelAsync()
        {
            try { await SendCommandAsync(MatchOp.Cancel, Array.Empty<byte>()).ConfigureAwait(false); } catch { /* best effort */ }
        }

        /// <summary>Sends a matchmaking command and maps protocol failures back to the public <see cref="MatchmakingException"/>; returns the player id from the reply.</summary>
        private async Task<string> SendCommandAsync(MatchOp op, byte[] body)
        {
            try
            {
                var reply = await _client.RequestRawAsync(Channels.Matchmaking, (ushort)op, body).ConfigureAwait(false);
                return MatchWire.DecodeReply(reply);
            }
            catch (ProtocolException ex) { throw new MatchmakingException(ex.Message); }
            catch (TimeoutException) { throw new MatchmakingException("Matchmaking command timed out."); }
        }

        private void OnEvent(string recipient, string queue, string roomCode, System.Collections.Generic.IReadOnlyList<string> players)
        {
            TaskCompletionSource<MatchResult>? tcs;
            string ownId;
            lock (_gate)
            {
                if (_waitingQueue == null || queue != _waitingQueue || recipient != _ownId) return;   // not for me
                tcs = _matchTcs;
                ownId = _ownId;
            }

            var result = new MatchResult(queue, roomCode, players, ownId);
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
}
