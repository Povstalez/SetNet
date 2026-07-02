using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;

namespace SetNet.Trade
{
    /// <summary>Reserved wire types for the trade service. Don't reuse these ids for application messages.</summary>
    public static class TradeTypes
    {
        /// <summary>Client → server: propose/offer/ready/confirm/cancel command.</summary>
        public const ushort Command = ushort.MaxValue - 49;   // 65486

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 50;     // 65485

        /// <summary>Server → client: push event (requested/updated/completed/cancelled).</summary>
        public const ushort Event = ushort.MaxValue - 51;     // 65484
    }

    internal enum TradeOp : byte { Propose = 0, Offer = 1, Ready = 2, Confirm = 3, Cancel = 4 }
    internal enum TradeEventType : byte { Requested = 0, Updated = 1, Completed = 2, Cancelled = 3 }

    /// <summary>The lifecycle stage of a trade.</summary>
    public enum TradeState
    {
        /// <summary>Both sides may still edit offers and toggle ready.</summary>
        Open = 0,

        /// <summary>Both sides are ready; offers are locked and each must confirm to finish.</summary>
        Confirming = 1,

        /// <summary>Items were swapped; the trade is finished.</summary>
        Completed = 2,

        /// <summary>The trade was cancelled; nothing moved.</summary>
        Cancelled = 3,
    }

    /// <summary>Thrown when a trade command fails (unknown trade, wrong state, target offline, timeout).</summary>
    public sealed class TradeException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public TradeException(string message) : base(message) { }
    }

    /// <summary>An immutable snapshot of a trade as seen from one participant's perspective.</summary>
    public sealed class TradeView
    {
        /// <summary>The trade's id.</summary>
        public string TradeId { get; internal set; } = "";

        /// <summary>The other participant's player key.</summary>
        public string PartnerKey { get; internal set; } = "";

        /// <summary>Items you are offering.</summary>
        public IReadOnlyList<ItemStack> YourOffer { get; internal set; } = Array.Empty<ItemStack>();

        /// <summary>Items your partner is offering.</summary>
        public IReadOnlyList<ItemStack> PartnerOffer { get; internal set; } = Array.Empty<ItemStack>();

        /// <summary>Whether you have marked ready.</summary>
        public bool YouReady { get; internal set; }

        /// <summary>Whether your partner has marked ready.</summary>
        public bool PartnerReady { get; internal set; }

        /// <summary>Whether you have confirmed (only meaningful in <see cref="TradeState.Confirming"/>).</summary>
        public bool YouConfirmed { get; internal set; }

        /// <summary>Whether your partner has confirmed.</summary>
        public bool PartnerConfirmed { get; internal set; }

        /// <summary>The current lifecycle stage.</summary>
        public TradeState State { get; internal set; }
    }

    // ---- wire ----

    internal sealed class TradeCommand
    {
        public int CorrelationId;
        public TradeOp Op;
        public string TradeId = "";
        public string TargetKey = "";     // Propose
        public string ItemId = "";        // Offer
        public long Count;                // Offer (0 removes)
        public bool Flag;                 // Ready value

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write((byte)Op);
            w.Write(TradeId ?? "");
            w.Write(TargetKey ?? "");
            w.Write(ItemId ?? "");
            w.Write(Count);
            w.Write(Flag);
            return ms.ToArray();
        }

        public static TradeCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new TradeCommand
            {
                CorrelationId = r.ReadInt32(),
                Op = (TradeOp)r.ReadByte(),
                TradeId = r.ReadString(),
                TargetKey = r.ReadString(),
                ItemId = r.ReadString(),
                Count = r.ReadInt64(),
                Flag = r.ReadBoolean(),
            };
        }
    }

    internal sealed class TradeReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string TradeId = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId);
            w.Write(Success);
            w.Write(Error ?? "");
            w.Write(TradeId ?? "");
            return ms.ToArray();
        }

        public static TradeReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new TradeReply
            {
                CorrelationId = r.ReadInt32(),
                Success = r.ReadBoolean(),
                Error = r.ReadString(),
                TradeId = r.ReadString(),
            };
        }
    }

    /// <summary>A trade event carrying a full view rendered for the recipient (so the client needs no local state).</summary>
    internal sealed class TradeEvent
    {
        public TradeEventType Type;
        public string TradeId = "";
        public string RecipientKey = "";
        public string PartnerKey = "";
        public List<ItemStack> YourOffer = new List<ItemStack>();
        public List<ItemStack> PartnerOffer = new List<ItemStack>();
        public bool YouReady, PartnerReady, YouConfirmed, PartnerConfirmed;
        public TradeState State;
        public string Reason = "";

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)Type);
            w.Write(TradeId ?? "");
            w.Write(RecipientKey ?? "");
            w.Write(PartnerKey ?? "");
            WriteStacks(w, YourOffer);
            WriteStacks(w, PartnerOffer);
            w.Write(YouReady); w.Write(PartnerReady); w.Write(YouConfirmed); w.Write(PartnerConfirmed);
            w.Write((byte)State);
            w.Write(Reason ?? "");
            return ms.ToArray();
        }

        public static TradeEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new TradeEvent
            {
                Type = (TradeEventType)r.ReadByte(),
                TradeId = r.ReadString(),
                RecipientKey = r.ReadString(),
                PartnerKey = r.ReadString(),
                YourOffer = ReadStacks(r),
                PartnerOffer = ReadStacks(r),
                YouReady = r.ReadBoolean(),
                PartnerReady = r.ReadBoolean(),
                YouConfirmed = r.ReadBoolean(),
                PartnerConfirmed = r.ReadBoolean(),
                State = (TradeState)r.ReadByte(),
                Reason = r.ReadString(),
            };
        }

        private static void WriteStacks(BinaryWriter w, List<ItemStack> stacks)
        {
            w.Write(stacks.Count);
            foreach (var s in stacks) { w.Write(s.ItemId ?? ""); w.Write(s.Count); }
        }

        private static List<ItemStack> ReadStacks(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<ItemStack>(count);
            for (var i = 0; i < count; i++) list.Add(new ItemStack(r.ReadString(), r.ReadInt64()));
            return list;
        }

        public TradeView ToView() => new TradeView
        {
            TradeId = TradeId,
            PartnerKey = PartnerKey,
            YourOffer = YourOffer,
            PartnerOffer = PartnerOffer,
            YouReady = YouReady,
            PartnerReady = PartnerReady,
            YouConfirmed = YouConfirmed,
            PartnerConfirmed = PartnerConfirmed,
            State = State,
        };
    }

    // ---- client ----

    internal static class TradeRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<TradeReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<TradeReply>>();
        private static readonly ConcurrentDictionary<TradeClient, byte> Clients
            = new ConcurrentDictionary<TradeClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<TradeReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, TradeReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(TradeClient c) => Clients[c] = 0;
        public static void DispatchEvent(TradeEvent evt) { foreach (var c in Clients.Keys) c.OnEvent(evt); }
    }

    /// <summary>
    /// Client-side trade driver, attached by <see cref="TradeClientExtensions.UseTrade"/>. Proposes a trade to
    /// another player, edits offers, and drives the two-phase confirm. The server is authoritative: items only move
    /// when <b>both</b> sides mark ready and then <b>both</b> confirm — the second phase locks the offers, so nobody
    /// can swap in a worse offer at the last instant.
    /// </summary>
    public sealed class TradeClient
    {
        private readonly BaseClient _client;
        private readonly string? _selfKey;
        private readonly object _gate = new object();
        private string? _tradeId;

        /// <summary>The id of the trade this client is currently in, or null.</summary>
        public string? TradeId { get { lock (_gate) return _tradeId; } }

        /// <summary>Raised when another player proposes a trade with you (args: their key, the trade view).</summary>
        public event Action<string, TradeView>? TradeRequested;

        /// <summary>Raised whenever the current trade changes (offer edited, ready/confirm toggled, state advanced).</summary>
        public event Action<TradeView>? Updated;

        /// <summary>Raised when the trade completes and items have swapped.</summary>
        public event Action<TradeView>? Completed;

        /// <summary>Raised when the trade is cancelled (arg: reason).</summary>
        public event Action<string>? Cancelled;

        internal TradeClient(BaseClient client, string? selfKey)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _selfKey = selfKey;
            TradeRegistry.RegisterClient(this);
        }

        /// <summary>Proposes a trade with another player (by their player key); returns the trade id.</summary>
        public async Task<string> ProposeAsync(string targetPlayerKey)
        {
            var reply = await SendCommand(new TradeCommand { Op = TradeOp.Propose, TargetKey = targetPlayerKey }).ConfigureAwait(false);
            lock (_gate) _tradeId = reply.TradeId;
            return reply.TradeId;
        }

        /// <summary>Adds/updates an offered item on your side (count 0 removes it). Editing resets both ready/confirm flags.</summary>
        public Task OfferAsync(string itemId, long count)
            => SendCommand(new TradeCommand { Op = TradeOp.Offer, TradeId = Require(), ItemId = itemId, Count = count });

        /// <summary>Marks (or clears) your ready flag. When both sides are ready the trade advances to confirming.</summary>
        public Task SetReadyAsync(bool ready)
            => SendCommand(new TradeCommand { Op = TradeOp.Ready, TradeId = Require(), Flag = ready });

        /// <summary>Confirms your side during the confirming phase. When both confirm, the server swaps the items.</summary>
        public Task ConfirmAsync()
            => SendCommand(new TradeCommand { Op = TradeOp.Confirm, TradeId = Require() });

        /// <summary>Cancels the trade. Tolerant of a dropped connection (the server auto-cancels on disconnect).</summary>
        public async Task CancelAsync()
        {
            var id = TradeId;
            if (id == null) return;
            try { await SendCommand(new TradeCommand { Op = TradeOp.Cancel, TradeId = id }).ConfigureAwait(false); }
            catch { /* already gone */ }
            lock (_gate) _tradeId = null;
        }

        private string Require()
        {
            var id = TradeId;
            if (id == null) throw new TradeException("Not currently in a trade.");
            return id;
        }

        private async Task<TradeReply> SendCommand(TradeCommand cmd)
        {
            var id = TradeRegistry.NextId();
            cmd.CorrelationId = id;
            var tcs = new TaskCompletionSource<TradeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            TradeRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(TradeTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    TradeReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new TradeException("Trade command timed out."); }
                    if (!reply.Success) throw new TradeException(reply.Error);
                    return reply;
                }
            }
            finally { TradeRegistry.Remove(id); }
        }

        internal void OnEvent(TradeEvent evt)
        {
            // Co-located clients share the static dispatch; when a self key is known, drop events addressed to a
            // different player (the routing that keeps two clients in one process from crossing perspectives).
            if (_selfKey != null && evt.RecipientKey != _selfKey) return;

            lock (_gate)
            {
                // Accept a Requested event for a new trade; otherwise only events for my current trade.
                if (evt.Type == TradeEventType.Requested) _tradeId = evt.TradeId;
                else if (_tradeId == null || _tradeId != evt.TradeId) return;

                if (evt.Type == TradeEventType.Completed || evt.Type == TradeEventType.Cancelled) _tradeId = null;
            }

            var view = evt.ToView();
            switch (evt.Type)
            {
                case TradeEventType.Requested: TradeRequested?.Invoke(evt.PartnerKey, view); break;
                case TradeEventType.Updated: Updated?.Invoke(view); break;
                case TradeEventType.Completed: Completed?.Invoke(view); break;
                case TradeEventType.Cancelled: Cancelled?.Invoke(evt.Reason); break;
            }
        }
    }

    // ---- server ----

    internal sealed class TradeSession
    {
        public string Id = "";
        public string A = "";
        public string B = "";
        public readonly Dictionary<string, long> OffersA = new Dictionary<string, long>();
        public readonly Dictionary<string, long> OffersB = new Dictionary<string, long>();
        public bool ReadyA, ReadyB, ConfirmA, ConfirmB;
        public TradeState State = TradeState.Open;
        public readonly object Gate = new object();

        public bool IsA(string key) => key == A;
        public Dictionary<string, long> OffersOf(string key) => key == A ? OffersA : OffersB;
        public string Other(string key) => key == A ? B : A;
    }

    /// <summary>
    /// Server-side trade hub, attached by <see cref="TradeServerExtensions.UseTrade"/>. Owns the trade state machine
    /// and performs the final atomic swap through the shared <see cref="InventoryServer"/> — items are revoked from
    /// each side and granted to the other only after both confirm; if anyone's holdings changed in the meantime the
    /// swap is rolled back and the trade cancelled.
    /// </summary>
    public sealed class TradeServer
    {
        private static readonly ConcurrentDictionary<BaseServer, TradeServer> Servers = new ConcurrentDictionary<BaseServer, TradeServer>();

        private readonly InventoryServer _inventory;
        private readonly ConcurrentDictionary<string, TradeSession> _sessions = new ConcurrentDictionary<string, TradeSession>();
        // Player key -> the trade they're in (one active trade per player).
        private readonly ConcurrentDictionary<string, string> _byPlayer = new ConcurrentDictionary<string, string>();

        internal TradeServer(InventoryServer inventory) => _inventory = inventory;

        internal static TradeServer Enable(BaseServer server, InventoryServer inventory)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new TradeServer(inventory);
                s.PeerDisconnected += peer => hub.CancelForPlayer(inventory.KeyOf(peer), "Partner disconnected.");
                return hub;
            });

        internal static TradeServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        internal async Task OnCommand(BasePeer peer, TradeCommand cmd)
        {
            var me = _inventory.KeyOf(peer);
            switch (cmd.Op)
            {
                case TradeOp.Propose: await Propose(peer, me, cmd); break;
                case TradeOp.Offer: await Mutate(peer, me, cmd, session => ApplyOffer(session, me, cmd.ItemId, cmd.Count)); break;
                case TradeOp.Ready: await Mutate(peer, me, cmd, session => ApplyReady(session, me, cmd.Flag)); break;
                case TradeOp.Confirm: await Confirm(peer, me, cmd); break;
                case TradeOp.Cancel: await Cancel(peer, me, cmd); break;
            }
        }

        private async Task Propose(BasePeer peer, string me, TradeCommand cmd)
        {
            var target = cmd.TargetKey ?? "";
            if (string.IsNullOrEmpty(target) || target == me)
            { await Reply(peer, cmd.CorrelationId, false, "Invalid trade target.", ""); return; }
            var targetPeer = _inventory.PeerFor(target);
            if (targetPeer == null)
            { await Reply(peer, cmd.CorrelationId, false, "That player is offline.", ""); return; }
            if (_byPlayer.ContainsKey(me) || _byPlayer.ContainsKey(target))
            { await Reply(peer, cmd.CorrelationId, false, "A participant is already trading.", ""); return; }

            var session = new TradeSession { Id = Guid.NewGuid().ToString("N"), A = me, B = target };
            if (!_sessions.TryAdd(session.Id, session))
            { await Reply(peer, cmd.CorrelationId, false, "Could not create trade.", ""); return; }
            _byPlayer[me] = session.Id;
            _byPlayer[target] = session.Id;

            await Reply(peer, cmd.CorrelationId, true, "", session.Id);
            // Invite the target; show the proposer its (empty) open trade — so only the invitee sees "requested".
            await PushOne(session, session.B, TradeEventType.Requested, "");
            await PushOne(session, session.A, TradeEventType.Updated, "");
        }

        private async Task Mutate(BasePeer peer, string me, TradeCommand cmd, Func<TradeSession, string?> apply)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session))
            { await Reply(peer, cmd.CorrelationId, false, "No such trade.", ""); return; }

            string? error;
            lock (session.Gate)
            {
                if (session.State != TradeState.Open) { error = "Trade is locked; cancel to change it."; }
                else error = apply(session);
            }
            if (error != null) { await Reply(peer, cmd.CorrelationId, false, error, session.Id); return; }

            await Reply(peer, cmd.CorrelationId, true, "", session.Id);
            await PushBoth(session, TradeEventType.Updated, "");
        }

        private static string? ApplyOffer(TradeSession session, string me, string itemId, long count)
        {
            if (string.IsNullOrEmpty(itemId)) return "Missing item id.";
            var offers = session.OffersOf(me);
            if (count <= 0) offers.Remove(itemId);
            else offers[itemId] = count;
            // Any change invalidates prior agreement.
            session.ReadyA = session.ReadyB = session.ConfirmA = session.ConfirmB = false;
            return null;
        }

        private static string? ApplyReady(TradeSession session, string me, bool ready)
        {
            if (session.IsA(me)) session.ReadyA = ready; else session.ReadyB = ready;
            if (session.ReadyA && session.ReadyB) session.State = TradeState.Confirming;
            return null;
        }

        private async Task Confirm(BasePeer peer, string me, TradeCommand cmd)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session))
            { await Reply(peer, cmd.CorrelationId, false, "No such trade.", ""); return; }

            bool bothConfirmed;
            lock (session.Gate)
            {
                if (session.State != TradeState.Confirming)
                { _ = Reply(peer, cmd.CorrelationId, false, "Both sides must be ready first.", session.Id); return; }
                if (session.IsA(me)) session.ConfirmA = true; else session.ConfirmB = true;
                bothConfirmed = session.ConfirmA && session.ConfirmB;
            }

            await Reply(peer, cmd.CorrelationId, true, "", session.Id);
            if (!bothConfirmed) { await PushBoth(session, TradeEventType.Updated, ""); return; }

            await ExecuteSwap(session);
        }

        /// <summary>Revokes both sides' offers atomically, then grants them crossed over; rolls back on any shortfall.</summary>
        private async Task ExecuteSwap(TradeSession session)
        {
            // Snapshot the offers under the lock; nothing can change them in Confirming state.
            List<KeyValuePair<string, long>> offersA, offersB;
            lock (session.Gate) { offersA = session.OffersA.ToList(); offersB = session.OffersB.ToList(); }

            var revoked = new List<(string Player, string Item, long Count)>();
            var ok = true;
            foreach (var (player, offers) in new[] { (session.A, offersA), (session.B, offersB) })
            {
                foreach (var item in offers)
                {
                    if (await _inventory.TryRevokeAsync(player, item.Key, item.Value).ConfigureAwait(false))
                        revoked.Add((player, item.Key, item.Value));
                    else { ok = false; break; }
                }
                if (!ok) break;
            }

            if (!ok)
            {
                // Compensate: give back everything we managed to take, then cancel.
                foreach (var r in revoked) await _inventory.GrantAsync(r.Player, r.Item, r.Count).ConfigureAwait(false);
                Remove(session);
                lock (session.Gate) session.State = TradeState.Cancelled;
                await PushBoth(session, TradeEventType.Cancelled, "A participant no longer has the offered items.");
                return;
            }

            // Cross-grant: A's items to B, B's items to A.
            foreach (var item in offersA) await _inventory.GrantAsync(session.B, item.Key, item.Value).ConfigureAwait(false);
            foreach (var item in offersB) await _inventory.GrantAsync(session.A, item.Key, item.Value).ConfigureAwait(false);

            Remove(session);
            lock (session.Gate) session.State = TradeState.Completed;
            await PushBoth(session, TradeEventType.Completed, "");
        }

        private async Task Cancel(BasePeer peer, string me, TradeCommand cmd)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session))
            { await Reply(peer, cmd.CorrelationId, true, "", ""); return; }   // already gone — treat as success
            await Reply(peer, cmd.CorrelationId, true, "", session.Id);
            Remove(session);
            lock (session.Gate) session.State = TradeState.Cancelled;
            await PushBoth(session, TradeEventType.Cancelled, "Cancelled by a participant.");
        }

        private void CancelForPlayer(string playerKey, string reason)
        {
            if (!_byPlayer.TryGetValue(playerKey, out var tradeId) || !_sessions.TryGetValue(tradeId, out var session)) return;
            Remove(session);
            lock (session.Gate) session.State = TradeState.Cancelled;
            _ = PushBoth(session, TradeEventType.Cancelled, reason);
        }

        private void Remove(TradeSession session)
        {
            _sessions.TryRemove(session.Id, out _);
            _byPlayer.TryRemove(session.A, out _);
            _byPlayer.TryRemove(session.B, out _);
        }

        private bool TryGetMySession(string me, string tradeId, out TradeSession session)
        {
            if (_sessions.TryGetValue(tradeId ?? "", out session!) && (session.A == me || session.B == me)) return true;
            session = null!;
            return false;
        }

        /// <summary>Renders and pushes a per-recipient view to each participant that's online.</summary>
        private async Task PushBoth(TradeSession session, TradeEventType type, string reason)
        {
            await PushOne(session, session.A, type, reason).ConfigureAwait(false);
            await PushOne(session, session.B, type, reason).ConfigureAwait(false);
        }

        private async Task PushOne(TradeSession session, string recipient, TradeEventType type, string reason)
        {
            var peer = _inventory.PeerFor(recipient);
            if (peer == null) return;
            var mine = session.OffersOf(recipient);
            var theirs = session.OffersOf(session.Other(recipient));
            bool youReady, partnerReady, youConfirmed, partnerConfirmed; TradeState state;
            lock (session.Gate)
            {
                var a = session.IsA(recipient);
                youReady = a ? session.ReadyA : session.ReadyB;
                partnerReady = a ? session.ReadyB : session.ReadyA;
                youConfirmed = a ? session.ConfirmA : session.ConfirmB;
                partnerConfirmed = a ? session.ConfirmB : session.ConfirmA;
                state = session.State;
            }
            var evt = new TradeEvent
            {
                Type = type,
                TradeId = session.Id,
                RecipientKey = recipient,
                PartnerKey = session.Other(recipient),
                YourOffer = mine.Select(kv => new ItemStack(kv.Key, kv.Value)).ToList(),
                PartnerOffer = theirs.Select(kv => new ItemStack(kv.Key, kv.Value)).ToList(),
                YouReady = youReady, PartnerReady = partnerReady,
                YouConfirmed = youConfirmed, PartnerConfirmed = partnerConfirmed,
                State = state, Reason = reason,
            };
            try { await peer.SendAsync(TradeTypes.Event, evt.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { /* dropped */ }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string err, string tradeId)
        {
            var reply = new TradeReply { CorrelationId = corr, Success = ok, Error = err, TradeId = tradeId };
            try { return peer.SendAsync(TradeTypes.Reply, reply.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for trade commands.</summary>
    [MessageHandler(TradeTypes.Command)]
    public sealed class TradeCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = TradeServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, TradeCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated trade replies.</summary>
    [MessageHandler(TradeTypes.Reply)]
    public sealed class TradeReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = TradeReply.Decode(data); TradeRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for trade push events.</summary>
    [MessageHandler(TradeTypes.Event)]
    public sealed class TradeEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { TradeRegistry.DispatchEvent(TradeEvent.Decode(data)); return Task.CompletedTask; }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the trade hub to a server by composition.</summary>
    public static class TradeServerExtensions
    {
        /// <summary>Enables the server-side trade hub. Pass the same <see cref="InventoryServer"/> returned by <c>UseInventory</c>.</summary>
        public static TradeServer UseTrade(this BaseServer server, InventoryServer inventory)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            return TradeServer.Enable(server, inventory);
        }
    }

    /// <summary>Attaches a trade driver to a client by composition.</summary>
    public static class TradeClientExtensions
    {
        /// <summary>
        /// Enables client-side trading; returns the driver (propose/offer/ready/confirm/cancel + events). Pass
        /// <paramref name="selfPlayerKey"/> — this player's key — only when several clients share one process (so
        /// perspective-rendered events route to the right one); a normal one-client-per-process app leaves it null.
        /// </summary>
        public static TradeClient UseTrade(this BaseClient client, string? selfPlayerKey = null) => new TradeClient(client, selfPlayerKey);
    }

    /// <summary>One-time bootstrap so the trade handlers are discovered. Call at startup.</summary>
    public static class TradeRuntime
    {
        /// <summary>Ensures the trade layer is discoverable.</summary>
        public static void Enable() { _ = TradeTypes.Command; }
    }
}
