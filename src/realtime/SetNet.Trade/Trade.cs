using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;

namespace SetNet.Trade
{
    /// <summary>Command operations (client → server) within the Trade protocol channel.</summary>
    internal enum TradeOp : ushort { Propose = 1, Offer = 2, Ready = 3, Confirm = 4, Cancel = 5 }

    /// <summary>Push events (server → client) within the Trade protocol channel.</summary>
    internal enum TradeEvt : ushort { Requested = 10, Updated = 11, Completed = 12, Cancelled = 13 }

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

    /// <summary>Decoded trade command body (the op and correlation live in the protocol envelope).</summary>
    internal sealed class TradeCommand
    {
        public string TradeId = "";
        public string TargetKey = "";     // Propose
        public string ItemId = "";        // Offer
        public long Count;                // Offer (0 removes)
        public bool Flag;                 // Ready value
    }

    /// <summary>A trade event carrying a full view rendered for the recipient (so the client needs no local state).</summary>
    internal sealed class TradeEvent
    {
        public TradeEvt Type;
        public string TradeId = "";
        public string RecipientKey = "";
        public string PartnerKey = "";
        public List<ItemStack> YourOffer = new List<ItemStack>();
        public List<ItemStack> PartnerOffer = new List<ItemStack>();
        public bool YouReady, PartnerReady, YouConfirmed, PartnerConfirmed;
        public TradeState State;
        public string Reason = "";

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

    /// <summary>Body codecs for the Trade channel (payload only; op/correlation are in the envelope).</summary>
    internal static class TradeWire
    {
        public static byte[] EncodeCommand(TradeCommand cmd)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(cmd.TradeId ?? "");
            w.Write(cmd.TargetKey ?? "");
            w.Write(cmd.ItemId ?? "");
            w.Write(cmd.Count);
            w.Write(cmd.Flag);
            return ms.ToArray();
        }

        public static TradeCommand DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new TradeCommand
            {
                TradeId = r.ReadString(),
                TargetKey = r.ReadString(),
                ItemId = r.ReadString(),
                Count = r.ReadInt64(),
                Flag = r.ReadBoolean(),
            };
        }

        public static byte[] EncodeReply(string tradeId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(tradeId ?? "");
            return ms.ToArray();
        }

        public static string DecodeReply(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }

        public static byte[] EncodeEvent(TradeEvent e)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(e.TradeId ?? "");
            w.Write(e.RecipientKey ?? "");
            w.Write(e.PartnerKey ?? "");
            WriteStacks(w, e.YourOffer);
            WriteStacks(w, e.PartnerOffer);
            w.Write(e.YouReady); w.Write(e.PartnerReady); w.Write(e.YouConfirmed); w.Write(e.PartnerConfirmed);
            w.Write((byte)e.State);
            w.Write(e.Reason ?? "");
            return ms.ToArray();
        }

        public static TradeEvent DecodeEvent(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new TradeEvent
            {
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
    }

    // ---- client ----

    /// <summary>
    /// Client-side trade driver, attached by <see cref="TradeClientExtensions.UseTrade"/>. Proposes a trade to
    /// another player, edits offers, and drives the two-phase confirm. The server is authoritative: items only move
    /// when <b>both</b> sides mark ready and then <b>both</b> confirm — the second phase locks the offers, so nobody
    /// can swap in a worse offer at the last instant. Rides the unified protocol on the <see cref="Channels.Trade"/> channel.
    /// </summary>
    public sealed class TradeClient
    {
        private readonly BaseClient _client;
        private readonly string? _selfKey;
        private readonly object _gate = new object();
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
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
            _subscriptions.Add(_client.OnRaw(Channels.Trade, (ushort)TradeEvt.Requested, b => OnEvent(TradeEvt.Requested, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Trade, (ushort)TradeEvt.Updated, b => OnEvent(TradeEvt.Updated, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Trade, (ushort)TradeEvt.Completed, b => OnEvent(TradeEvt.Completed, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Trade, (ushort)TradeEvt.Cancelled, b => OnEvent(TradeEvt.Cancelled, b)));
        }

        /// <summary>Proposes a trade with another player (by their player key); returns the trade id.</summary>
        public async Task<string> ProposeAsync(string targetPlayerKey)
        {
            var tradeId = await SendCommand(TradeOp.Propose, new TradeCommand { TargetKey = targetPlayerKey }).ConfigureAwait(false);
            lock (_gate) _tradeId = tradeId;
            return tradeId;
        }

        /// <summary>Adds/updates an offered item on your side (count 0 removes it). Editing resets both ready/confirm flags.</summary>
        public Task OfferAsync(string itemId, long count)
            => SendCommand(TradeOp.Offer, new TradeCommand { TradeId = Require(), ItemId = itemId, Count = count });

        /// <summary>Marks (or clears) your ready flag. When both sides are ready the trade advances to confirming.</summary>
        public Task SetReadyAsync(bool ready)
            => SendCommand(TradeOp.Ready, new TradeCommand { TradeId = Require(), Flag = ready });

        /// <summary>Confirms your side during the confirming phase. When both confirm, the server swaps the items.</summary>
        public Task ConfirmAsync()
            => SendCommand(TradeOp.Confirm, new TradeCommand { TradeId = Require() });

        /// <summary>Cancels the trade. Tolerant of a dropped connection (the server auto-cancels on disconnect).</summary>
        public async Task CancelAsync()
        {
            var id = TradeId;
            if (id == null) return;
            try { await SendCommand(TradeOp.Cancel, new TradeCommand { TradeId = id }).ConfigureAwait(false); }
            catch { /* already gone */ }
            lock (_gate) _tradeId = null;
        }

        private string Require()
        {
            var id = TradeId;
            if (id == null) throw new TradeException("Not currently in a trade.");
            return id;
        }

        private async Task<string> SendCommand(TradeOp op, TradeCommand cmd)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Trade, (ushort)op, TradeWire.EncodeCommand(cmd)).ConfigureAwait(false);
                return TradeWire.DecodeReply(body);
            }
            catch (ProtocolException ex) { throw new TradeException(ex.Message); }
            catch (TimeoutException) { throw new TradeException("Trade command timed out."); }
        }

        private void OnEvent(TradeEvt type, byte[] body)
        {
            var evt = TradeWire.DecodeEvent(body);
            evt.Type = type;

            // Co-located clients share the static dispatch; when a self key is known, drop events addressed to a
            // different player (the routing that keeps two clients in one process from crossing perspectives).
            if (_selfKey != null && evt.RecipientKey != _selfKey) return;

            lock (_gate)
            {
                // Accept a Requested event for a new trade; otherwise only events for my current trade.
                if (type == TradeEvt.Requested) _tradeId = evt.TradeId;
                else if (_tradeId == null || _tradeId != evt.TradeId) return;

                if (type == TradeEvt.Completed || type == TradeEvt.Cancelled) _tradeId = null;
            }

            var view = evt.ToView();
            switch (type)
            {
                case TradeEvt.Requested: TradeRequested?.Invoke(evt.PartnerKey, view); break;
                case TradeEvt.Updated: Updated?.Invoke(view); break;
                case TradeEvt.Completed: Completed?.Invoke(view); break;
                case TradeEvt.Cancelled: Cancelled?.Invoke(evt.Reason); break;
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

        internal async Task HandleAsync(ChannelRequest request)
        {
            var me = _inventory.KeyOf(request.Peer);
            var cmd = TradeWire.DecodeCommand(request.RawBody);
            switch ((TradeOp)request.Op)
            {
                case TradeOp.Propose: await Propose(request, me, cmd); break;
                case TradeOp.Offer: await Mutate(request, me, cmd, session => ApplyOffer(session, me, cmd.ItemId, cmd.Count)); break;
                case TradeOp.Ready: await Mutate(request, me, cmd, session => ApplyReady(session, me, cmd.Flag)); break;
                case TradeOp.Confirm: await Confirm(request, me, cmd); break;
                case TradeOp.Cancel: await Cancel(request, me, cmd); break;
            }
        }

        private async Task Propose(ChannelRequest request, string me, TradeCommand cmd)
        {
            var target = cmd.TargetKey ?? "";
            if (string.IsNullOrEmpty(target) || target == me) throw new ProtocolException("Invalid trade target.");
            var targetPeer = _inventory.PeerFor(target);
            if (targetPeer == null) throw new ProtocolException("That player is offline.");
            if (_byPlayer.ContainsKey(me) || _byPlayer.ContainsKey(target)) throw new ProtocolException("A participant is already trading.");

            var session = new TradeSession { Id = Guid.NewGuid().ToString("N"), A = me, B = target };
            if (!_sessions.TryAdd(session.Id, session)) throw new ProtocolException("Could not create trade.");
            _byPlayer[me] = session.Id;
            _byPlayer[target] = session.Id;

            await request.ReplyRawAsync(TradeWire.EncodeReply(session.Id)).ConfigureAwait(false);
            // Invite the target; show the proposer its (empty) open trade — so only the invitee sees "requested".
            await PushOne(session, session.B, TradeEvt.Requested, "").ConfigureAwait(false);
            await PushOne(session, session.A, TradeEvt.Updated, "").ConfigureAwait(false);
        }

        private async Task Mutate(ChannelRequest request, string me, TradeCommand cmd, Func<TradeSession, string?> apply)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session)) throw new ProtocolException("No such trade.");

            string? error;
            lock (session.Gate)
            {
                if (session.State != TradeState.Open) error = "Trade is locked; cancel to change it.";
                else error = apply(session);
            }
            if (error != null) throw new ProtocolException(error);

            await request.ReplyRawAsync(TradeWire.EncodeReply(session.Id)).ConfigureAwait(false);
            await PushBoth(session, TradeEvt.Updated, "").ConfigureAwait(false);
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

        private async Task Confirm(ChannelRequest request, string me, TradeCommand cmd)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session)) throw new ProtocolException("No such trade.");

            bool bothConfirmed;
            lock (session.Gate)
            {
                if (session.State != TradeState.Confirming) throw new ProtocolException("Both sides must be ready first.");
                if (session.IsA(me)) session.ConfirmA = true; else session.ConfirmB = true;
                bothConfirmed = session.ConfirmA && session.ConfirmB;
            }

            await request.ReplyRawAsync(TradeWire.EncodeReply(session.Id)).ConfigureAwait(false);
            if (!bothConfirmed) { await PushBoth(session, TradeEvt.Updated, "").ConfigureAwait(false); return; }

            await ExecuteSwap(session).ConfigureAwait(false);
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
                await PushBoth(session, TradeEvt.Cancelled, "A participant no longer has the offered items.").ConfigureAwait(false);
                return;
            }

            // Cross-grant: A's items to B, B's items to A.
            foreach (var item in offersA) await _inventory.GrantAsync(session.B, item.Key, item.Value).ConfigureAwait(false);
            foreach (var item in offersB) await _inventory.GrantAsync(session.A, item.Key, item.Value).ConfigureAwait(false);

            Remove(session);
            lock (session.Gate) session.State = TradeState.Completed;
            await PushBoth(session, TradeEvt.Completed, "").ConfigureAwait(false);
        }

        private async Task Cancel(ChannelRequest request, string me, TradeCommand cmd)
        {
            if (!TryGetMySession(me, cmd.TradeId, out var session))
            { await request.ReplyRawAsync(TradeWire.EncodeReply("")).ConfigureAwait(false); return; }   // already gone — treat as success
            await request.ReplyRawAsync(TradeWire.EncodeReply(session.Id)).ConfigureAwait(false);
            Remove(session);
            lock (session.Gate) session.State = TradeState.Cancelled;
            await PushBoth(session, TradeEvt.Cancelled, "Cancelled by a participant.").ConfigureAwait(false);
        }

        private void CancelForPlayer(string playerKey, string reason)
        {
            if (!_byPlayer.TryGetValue(playerKey, out var tradeId) || !_sessions.TryGetValue(tradeId, out var session)) return;
            Remove(session);
            lock (session.Gate) session.State = TradeState.Cancelled;
            _ = PushBoth(session, TradeEvt.Cancelled, reason);
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
        private async Task PushBoth(TradeSession session, TradeEvt type, string reason)
        {
            await PushOne(session, session.A, type, reason).ConfigureAwait(false);
            await PushOne(session, session.B, type, reason).ConfigureAwait(false);
        }

        private async Task PushOne(TradeSession session, string recipient, TradeEvt type, string reason)
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
            try { await peer.PublishRawAsync(Channels.Trade, (ushort)type, TradeWire.EncodeEvent(evt)).ConfigureAwait(false); } catch { /* dropped */ }
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for trade commands.</summary>
    [ProtocolChannel(Channels.Trade)]
    public sealed class TradeChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = TradeServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("trade is not configured on this server");
            return hub.HandleAsync(request);
        }
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

    /// <summary>One-time bootstrap so the trade channel service is discovered. Call at startup.</summary>
    public static class TradeRuntime
    {
        /// <summary>Ensures the trade layer is discoverable.</summary>
        public static void Enable() { _ = typeof(TradeChannelService); }
    }
}
