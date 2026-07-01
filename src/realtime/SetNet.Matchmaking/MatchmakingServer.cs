using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Rooms;

namespace SetNet.Matchmaking
{
    /// <summary>A player waiting in a queue: the peer, its skill, and when it entered (monotonic ticks) so its acceptance window can widen.</summary>
    internal sealed class Ticket
    {
        public readonly BasePeer Peer;
        public readonly Guid PeerId;
        public readonly string PlayerId;
        public readonly int Skill;
        public readonly long EnqueuedTicks;

        public Ticket(BasePeer peer, int skill, long enqueuedTicks)
        {
            Peer = peer;
            PeerId = peer.CurrentPeerInfo.Id;
            PlayerId = peer.CurrentPeerInfo.Id.ToString("N");
            Skill = skill;
            EnqueuedTicks = enqueuedTicks;
        }

        /// <summary>The current ± skill spread this player will accept, widening the longer it waits.</summary>
        public double CurrentWindow(MatchmakingOptions options, long nowTicks)
        {
            if (!options.UseSkill) return double.MaxValue;   // FIFO — everyone is acceptable
            var waitedSeconds = Math.Max(0, (nowTicks - EnqueuedTicks) / (double)Stopwatch.Frequency);
            return options.BaseSkillWindow + options.SkillWindowGrowthPerSecond * waitedSeconds;
        }
    }

    /// <summary>Per-server matchmaking state: the shared room store, options, per-queue waiting tickets, and the match ticker.</summary>
    internal sealed class MatchmakingServerState : IDisposable
    {
        public IRoomStore Store = null!;
        public MatchmakingOptions Options = null!;

        // queue name -> (peer id -> ticket). Concurrent so enqueue/cancel/disconnect and the ticker can all touch it.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Ticket>> _queues
            = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Ticket>>();
        // peer id -> its queue name, so cancel/disconnect can find and remove a ticket by peer alone.
        private readonly ConcurrentDictionary<Guid, string> _peerQueue = new ConcurrentDictionary<Guid, string>();

        private Timer? _timer;
        private int _ticking;

        public void Start()
        {
            var period = Math.Max(50, Options.TickIntervalMs);
            _timer = new Timer(_ => _ = TickAsync(), null, period, period);
        }

        public string Enqueue(BasePeer peer, string queue, int skill)
        {
            var ticket = new Ticket(peer, skill, Stopwatch.GetTimestamp());
            RemovePeer(peer.CurrentPeerInfo.Id);   // a peer waits in at most one queue
            var bucket = _queues.GetOrAdd(queue, _ => new ConcurrentDictionary<Guid, Ticket>());
            bucket[ticket.PeerId] = ticket;
            _peerQueue[ticket.PeerId] = queue;
            return ticket.PlayerId;
        }

        public void RemovePeer(Guid peerId)
        {
            if (_peerQueue.TryRemove(peerId, out var queue) && _queues.TryGetValue(queue, out var bucket))
                bucket.TryRemove(peerId, out _);
        }

        /// <summary>One matchmaking pass over every queue. Reentrancy-guarded so ticks never overlap.</summary>
        private async Task TickAsync()
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;   // a previous tick is still running
            try
            {
                foreach (var pair in _queues)
                    await FormMatchesAsync(pair.Key, pair.Value).ConfigureAwait(false);
            }
            catch { /* never let a tick throw on the timer thread */ }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        private async Task FormMatchesAsync(string queue, ConcurrentDictionary<Guid, Ticket> bucket)
        {
            var size = Math.Max(1, Options.MatchSize);
            if (bucket.Count < size) return;

            var now = Stopwatch.GetTimestamp();
            var waiting = new List<Ticket>(bucket.Values);
            waiting.Sort((a, b) => a.Skill.CompareTo(b.Skill));   // sort by skill so similar players sit adjacent

            var i = 0;
            while (i + size <= waiting.Count)
            {
                var group = waiting.GetRange(i, size);
                var spread = group[size - 1].Skill - group[0].Skill;

                // The group is acceptable if its skill spread fits inside every member's current (widening) window.
                var allowed = double.MaxValue;
                foreach (var t in group) allowed = Math.Min(allowed, t.CurrentWindow(Options, now));

                if (spread <= allowed && TryClaim(bucket, group))
                {
                    await AnnounceMatchAsync(queue, group).ConfigureAwait(false);
                    i += size;
                }
                else
                {
                    i++;   // try starting the window one player later
                }
            }
        }

        /// <summary>Atomically removes all of a group's tickets from the queue; rolls back and fails if any is already gone.</summary>
        private static bool TryClaim(ConcurrentDictionary<Guid, Ticket> bucket, List<Ticket> group)
        {
            var claimed = new List<Ticket>(group.Count);
            foreach (var t in group)
            {
                if (bucket.TryRemove(t.PeerId, out var removed)) claimed.Add(removed);
                else { foreach (var back in claimed) bucket[back.PeerId] = back; return false; }
            }
            return true;
        }

        private async Task AnnounceMatchAsync(string queue, List<Ticket> group)
        {
            foreach (var t in group) _peerQueue.TryRemove(t.PeerId, out _);

            var maxPlayers = Options.MatchedRoomMaxPlayers > 0 ? Options.MatchedRoomMaxPlayers : Options.MatchSize;
            var room = await Store.CreateAsync(maxPlayers).ConfigureAwait(false);

            var players = new List<string>(group.Count);
            foreach (var t in group) players.Add(t.PlayerId);

            foreach (var t in group)
            {
                var evt = new MatchEvent(t.PlayerId, queue, room.Code, players);
                try { await t.Peer.SendAsync(MatchTypes.Event, evt.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false); }
                catch { /* member dropping; the empty room will be cleaned up by Rooms when others leave */ }
            }
        }

        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>
    /// Server-side matchmaking entry point. Call <see cref="UseMatchmaking"/> once after constructing your server,
    /// passing the <b>same</b> <see cref="IRoomStore"/> you gave <c>server.UseRooms(store)</c> — matched players are
    /// dropped into a freshly created room in that store, which they then join via their <c>RoomsClient</c>. No base
    /// class needed.
    /// </summary>
    public static class MatchmakingServer
    {
        private static readonly ConcurrentDictionary<BaseServer, MatchmakingServerState> Servers
            = new ConcurrentDictionary<BaseServer, MatchmakingServerState>();

        /// <summary>Enables matchmaking on a server. <paramref name="store"/> must be the same room store used by <c>UseRooms</c>.</summary>
        public static void UseMatchmaking(this BaseServer server, IRoomStore store, MatchmakingOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (store == null) throw new ArgumentNullException(nameof(store));

            var state = new MatchmakingServerState { Store = store, Options = options ?? new MatchmakingOptions() };
            Servers[server] = state;
            server.PeerDisconnected += peer => state.RemovePeer(peer.CurrentPeerInfo.Id);   // drop a leaver from its queue
            state.Start();
        }

        internal static MatchmakingServerState? Get(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var state) ? state : null;
    }

    /// <summary>Auto-discovered handler for matchmaking commands (enqueue/cancel). Serializer-agnostic (byte[]).</summary>
    [MessageHandler(MatchTypes.Command)]
    public sealed class MatchmakingServerHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public async Task HandleAsync(BasePeer peer, byte[] data)
        {
            var cmd = MatchCommand.Decode(data);
            var state = MatchmakingServer.Get(peer.CurrentPeerInfo.Server);
            if (state == null)
            {
                await ReplyAsync(peer, MatchReply.Fail(cmd.CorrelationId, "matchmaking is not configured on this server")).ConfigureAwait(false);
                return;
            }

            switch (cmd.Op)
            {
                case MatchOp.Enqueue:
                {
                    var playerId = state.Enqueue(peer, cmd.Queue, cmd.Skill);
                    await ReplyAsync(peer, MatchReply.Ok(cmd.CorrelationId, playerId)).ConfigureAwait(false);
                    break;
                }
                case MatchOp.Cancel:
                {
                    state.RemovePeer(peer.CurrentPeerInfo.Id);
                    await ReplyAsync(peer, MatchReply.Ok(cmd.CorrelationId, peer.CurrentPeerInfo.Id.ToString("N"))).ConfigureAwait(false);
                    break;
                }
            }
        }

        private static Task ReplyAsync(BasePeer peer, MatchReply reply)
            => peer.SendAsync(MatchTypes.Reply, reply.Encode(), DeliveryMethod.Reliable);
    }
}
