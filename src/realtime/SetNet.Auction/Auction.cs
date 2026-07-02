using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;
using SetNet.Wallet;

namespace SetNet.Auction
{
    /// <summary>Reserved wire types for the auction service. Don't reuse these ids for application messages.</summary>
    public static class AuctionTypes
    {
        /// <summary>Client → server: browse/sell/bid/buyout/cancel command.</summary>
        public const ushort Command = ushort.MaxValue - 63;   // 65472

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 64;     // 65471

        /// <summary>Server → client: push event (outbid/won/sold/returned).</summary>
        public const ushort Event = ushort.MaxValue - 65;     // 65470
    }

    internal enum AuctionOp : byte { Browse = 0, Sell = 1, Bid = 2, Buyout = 3, Cancel = 4 }

    /// <summary>What happened to an auction the player is involved in.</summary>
    public enum AuctionEventType : byte
    {
        /// <summary>You were outbid; your bid was refunded.</summary>
        Outbid = 0,

        /// <summary>You won the auction; the item is in your inventory.</summary>
        Won = 1,

        /// <summary>Your listing sold; the proceeds are in your wallet.</summary>
        Sold = 2,

        /// <summary>Your listing ended with no buyer; the item was returned to you.</summary>
        Returned = 3,
    }

    /// <summary>Thrown when an auction operation fails (unknown listing, bid too low, can't afford, timeout).</summary>
    public sealed class AuctionException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public AuctionException(string message) : base(message) { }
    }

    /// <summary>A public view of an active auction listing.</summary>
    public sealed class AuctionListing
    {
        /// <summary>Listing id.</summary>
        public string Id { get; set; } = "";

        /// <summary>Seller's player key.</summary>
        public string Seller { get; set; } = "";

        /// <summary>Item on sale.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>Quantity on sale.</summary>
        public long Count { get; set; }

        /// <summary>Bid currency.</summary>
        public string Currency { get; set; } = "gold";

        /// <summary>Minimum first bid.</summary>
        public long MinBid { get; set; }

        /// <summary>Instant-buy price (0 = no buyout).</summary>
        public long Buyout { get; set; }

        /// <summary>Current highest bid (0 = none yet).</summary>
        public long CurrentBid { get; set; }

        /// <summary>Seconds remaining until the listing settles.</summary>
        public long SecondsLeft { get; set; }
    }

    // ---- server-side listing ----

    internal sealed class Listing
    {
        public string Id = "";
        public string Seller = "";
        public string ItemId = "";
        public long Count;
        public string Currency = "gold";
        public long MinBid;
        public long Buyout;
        public long CurrentBid;
        public string? CurrentBidder;
        public long ExpiresTicks;
        public bool Settled;
        public readonly object Gate = new object();
    }

    // ---- wire ----

    internal sealed class AuctionCommand
    {
        public int CorrelationId;
        public AuctionOp Op;
        public string ListingId = "";
        public string ItemId = "";
        public long Count;
        public string Currency = "gold";
        public long MinBid;
        public long Buyout;
        public long Amount;         // bid amount
        public int DurationSeconds;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write((byte)Op); w.Write(ListingId ?? ""); w.Write(ItemId ?? ""); w.Write(Count);
            w.Write(Currency ?? ""); w.Write(MinBid); w.Write(Buyout); w.Write(Amount); w.Write(DurationSeconds);
            return ms.ToArray();
        }

        public static AuctionCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new AuctionCommand
            {
                CorrelationId = r.ReadInt32(), Op = (AuctionOp)r.ReadByte(), ListingId = r.ReadString(), ItemId = r.ReadString(), Count = r.ReadInt64(),
                Currency = r.ReadString(), MinBid = r.ReadInt64(), Buyout = r.ReadInt64(), Amount = r.ReadInt64(), DurationSeconds = r.ReadInt32(),
            };
        }
    }

    internal sealed class AuctionReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string ListingId = "";
        public List<AuctionListing> Listings = new List<AuctionListing>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write(Success); w.Write(Error ?? ""); w.Write(ListingId ?? "");
            w.Write(Listings.Count);
            foreach (var l in Listings)
            {
                w.Write(l.Id ?? ""); w.Write(l.Seller ?? ""); w.Write(l.ItemId ?? ""); w.Write(l.Count);
                w.Write(l.Currency ?? ""); w.Write(l.MinBid); w.Write(l.Buyout); w.Write(l.CurrentBid); w.Write(l.SecondsLeft);
            }
            return ms.ToArray();
        }

        public static AuctionReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new AuctionReply { CorrelationId = r.ReadInt32(), Success = r.ReadBoolean(), Error = r.ReadString(), ListingId = r.ReadString() };
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
                reply.Listings.Add(new AuctionListing
                {
                    Id = r.ReadString(), Seller = r.ReadString(), ItemId = r.ReadString(), Count = r.ReadInt64(),
                    Currency = r.ReadString(), MinBid = r.ReadInt64(), Buyout = r.ReadInt64(), CurrentBid = r.ReadInt64(), SecondsLeft = r.ReadInt64(),
                });
            return reply;
        }
    }

    /// <summary>An auction push event about a listing you're involved in.</summary>
    public sealed class AuctionEvent
    {
        /// <summary>What happened.</summary>
        public AuctionEventType Type { get; internal set; }

        /// <summary>The listing id.</summary>
        public string ListingId { get; internal set; } = "";

        /// <summary>The item id (for Won/Returned).</summary>
        public string ItemId { get; internal set; } = "";

        /// <summary>The item quantity (for Won/Returned).</summary>
        public long Count { get; internal set; }

        /// <summary>The amount of currency involved (refund for Outbid, proceeds for Sold).</summary>
        public long Amount { get; internal set; }

        /// <summary>The currency id.</summary>
        public string Currency { get; internal set; } = "gold";

        internal byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)Type); w.Write(ListingId ?? ""); w.Write(ItemId ?? ""); w.Write(Count); w.Write(Amount); w.Write(Currency ?? "");
            return ms.ToArray();
        }

        internal static AuctionEvent Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new AuctionEvent { Type = (AuctionEventType)r.ReadByte(), ListingId = r.ReadString(), ItemId = r.ReadString(), Count = r.ReadInt64(), Amount = r.ReadInt64(), Currency = r.ReadString() };
        }
    }

    internal static class AuctionRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<AuctionReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<AuctionReply>>();
        private static readonly ConcurrentDictionary<AuctionClient, byte> Clients = new ConcurrentDictionary<AuctionClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<AuctionReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, AuctionReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(AuctionClient c) => Clients[c] = 0;
        public static void DispatchEvent(AuctionEvent evt) { foreach (var c in Clients.Keys) c.OnEvent(evt); }
    }

    /// <summary>
    /// Client-side auction driver, attached by <see cref="AuctionClientExtensions.UseAuction"/>. Browse listings,
    /// put items up for sale (escrowed from your inventory), bid or buy out (currency escrowed, prior bidder
    /// refunded), and cancel your own bid-free listings. Outcomes arrive via <see cref="Outbid"/>/<see cref="Won"/>/<see cref="Sold"/>/<see cref="Returned"/>.
    /// </summary>
    public sealed class AuctionClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when someone outbids you (your bid was refunded); arg carries the refund amount.</summary>
        public event Action<AuctionEvent>? Outbid;

        /// <summary>Raised when you win a listing (item granted to your inventory).</summary>
        public event Action<AuctionEvent>? Won;

        /// <summary>Raised when your listing sells (proceeds credited to your wallet).</summary>
        public event Action<AuctionEvent>? Sold;

        /// <summary>Raised when your listing ends unsold (item returned to you).</summary>
        public event Action<AuctionEvent>? Returned;

        internal AuctionClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            AuctionRegistry.RegisterClient(this);
        }

        /// <summary>Lists all active auctions.</summary>
        public async Task<IReadOnlyList<AuctionListing>> BrowseAsync()
            => (await Send(new AuctionCommand { Op = AuctionOp.Browse }).ConfigureAwait(false)).Listings;

        /// <summary>Puts an item up for auction (escrowed from your inventory); returns the listing id.</summary>
        public async Task<string> SellAsync(string itemId, long count, long minBid, int durationSeconds, long buyout = 0, string currency = "gold")
        {
            var reply = await Send(new AuctionCommand { Op = AuctionOp.Sell, ItemId = itemId, Count = count, MinBid = minBid, Buyout = buyout, Currency = currency, DurationSeconds = durationSeconds }).ConfigureAwait(false);
            return reply.ListingId;
        }

        /// <summary>Places a bid (must beat the current bid / meet the minimum); your currency is escrowed.</summary>
        public Task BidAsync(string listingId, long amount) => Send(new AuctionCommand { Op = AuctionOp.Bid, ListingId = listingId, Amount = amount });

        /// <summary>Buys a listing outright at its buyout price and settles immediately.</summary>
        public Task BuyoutAsync(string listingId) => Send(new AuctionCommand { Op = AuctionOp.Buyout, ListingId = listingId });

        /// <summary>Cancels your own listing (only allowed while it has no bids); the item is returned.</summary>
        public Task CancelAsync(string listingId) => Send(new AuctionCommand { Op = AuctionOp.Cancel, ListingId = listingId });

        private async Task<AuctionReply> Send(AuctionCommand cmd)
        {
            var id = AuctionRegistry.NextId();
            cmd.CorrelationId = id;
            var tcs = new TaskCompletionSource<AuctionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            AuctionRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(AuctionTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    AuctionReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new AuctionException("Auction command timed out."); }
                    if (!reply.Success) throw new AuctionException(reply.Error);
                    return reply;
                }
            }
            finally { AuctionRegistry.Remove(id); }
        }

        internal void OnEvent(AuctionEvent evt)
        {
            switch (evt.Type)
            {
                case AuctionEventType.Outbid: Outbid?.Invoke(evt); break;
                case AuctionEventType.Won: Won?.Invoke(evt); break;
                case AuctionEventType.Sold: Sold?.Invoke(evt); break;
                case AuctionEventType.Returned: Returned?.Invoke(evt); break;
            }
        }
    }

    /// <summary>
    /// Server-side auction house, attached by <see cref="AuctionServerExtensions.UseAuction"/>. Escrows the seller's
    /// item at listing time and each bidder's currency at bid time (refunding the previous high bidder), and a
    /// background timer settles expired listings: the winner gets the item, the seller gets the winning bid, or the
    /// item is returned if nobody bid. All item/currency moves go through the shared <see cref="InventoryServer"/> /
    /// <see cref="WalletServer"/>, so escrow can't dupe or lose anything.
    /// </summary>
    public sealed class AuctionServer : IDisposable
    {
        private static readonly ConcurrentDictionary<BaseServer, AuctionServer> Servers = new ConcurrentDictionary<BaseServer, AuctionServer>();

        private readonly InventoryServer _inventory;
        private readonly WalletServer _wallet;
        private readonly ConcurrentDictionary<string, Listing> _listings = new ConcurrentDictionary<string, Listing>();
        private readonly Timer _timer;

        internal AuctionServer(InventoryServer inventory, WalletServer wallet)
        {
            _inventory = inventory;
            _wallet = wallet;
            _timer = new Timer(_ => _ = SettleExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        internal static AuctionServer Enable(BaseServer server, InventoryServer inventory, WalletServer wallet)
            => Servers.GetOrAdd(server, _ => new AuctionServer(inventory, wallet));

        internal static AuctionServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        internal async Task OnCommand(BasePeer peer, AuctionCommand cmd)
        {
            var me = _inventory.KeyOf(peer);
            try
            {
                switch (cmd.Op)
                {
                    case AuctionOp.Browse: await Reply(peer, cmd.CorrelationId, true, "", "", Snapshot()); break;
                    case AuctionOp.Sell: await Sell(peer, me, cmd); break;
                    case AuctionOp.Bid: await Bid(peer, me, cmd, cmd.Amount, buyout: false); break;
                    case AuctionOp.Buyout: await Buyout(peer, me, cmd); break;
                    case AuctionOp.Cancel: await Cancel(peer, me, cmd); break;
                }
            }
            catch (AuctionException ex) { await Reply(peer, cmd.CorrelationId, false, ex.Message, "", null); }
        }

        private async Task Sell(BasePeer peer, string me, AuctionCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Count <= 0 || cmd.MinBid < 0 || cmd.DurationSeconds <= 0)
            { await Reply(peer, cmd.CorrelationId, false, "Invalid listing.", "", null); return; }

            // Escrow the item out of the seller's inventory.
            if (!await _inventory.TryRevokeAsync(me, cmd.ItemId, cmd.Count).ConfigureAwait(false))
            { await Reply(peer, cmd.CorrelationId, false, "You don't have that item.", "", null); return; }

            var listing = new Listing
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Seller = me, ItemId = cmd.ItemId, Count = cmd.Count, Currency = string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency,
                MinBid = cmd.MinBid, Buyout = cmd.Buyout,
                ExpiresTicks = Stopwatch.GetTimestamp() + (long)(cmd.DurationSeconds * (double)Stopwatch.Frequency),
            };
            _listings[listing.Id] = listing;
            await Reply(peer, cmd.CorrelationId, true, "", listing.Id, null);
        }

        private async Task Bid(BasePeer peer, string me, AuctionCommand cmd, long amount, bool buyout)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new AuctionException("No such listing.");
            if (listing.Seller == me) throw new AuctionException("You can't bid on your own listing.");

            string? refundBidder = null; long refundAmount = 0;
            lock (listing.Gate)
            {
                if (listing.Settled) throw new AuctionException("Listing already ended.");
                var minRequired = listing.CurrentBidder == null ? listing.MinBid : listing.CurrentBid + 1;
                if (!buyout && amount < minRequired) throw new AuctionException($"Bid must be at least {minRequired}.");
                refundBidder = listing.CurrentBidder;
                refundAmount = listing.CurrentBid;
            }

            // Escrow the new bid before committing it as the high bid.
            if (!await _wallet.TryWithdrawAsync(me, listing.Currency, amount).ConfigureAwait(false))
                throw new AuctionException($"Not enough {listing.Currency}.");

            // Refund the previous high bidder (their escrow returns).
            if (refundBidder != null && refundAmount > 0)
            {
                await _wallet.DepositAsync(refundBidder, listing.Currency, refundAmount).ConfigureAwait(false);
                await Notify(refundBidder, new AuctionEvent { Type = AuctionEventType.Outbid, ListingId = listing.Id, Amount = refundAmount, Currency = listing.Currency }).ConfigureAwait(false);
            }

            lock (listing.Gate) { listing.CurrentBid = amount; listing.CurrentBidder = me; }

            if (buyout) await Settle(listing).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", listing.Id, null);
        }

        private async Task Buyout(BasePeer peer, string me, AuctionCommand cmd)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new AuctionException("No such listing.");
            if (listing.Buyout <= 0) throw new AuctionException("This listing has no buyout.");
            await Bid(peer, me, cmd, listing.Buyout, buyout: true).ConfigureAwait(false);
        }

        private async Task Cancel(BasePeer peer, string me, AuctionCommand cmd)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new AuctionException("No such listing.");
            if (listing.Seller != me) throw new AuctionException("Not your listing.");
            bool hasBid; lock (listing.Gate) { if (listing.Settled) throw new AuctionException("Listing already ended."); hasBid = listing.CurrentBidder != null; }
            if (hasBid) throw new AuctionException("Can't cancel a listing that has bids.");

            if (TryClaimSettle(listing))
            {
                _listings.TryRemove(listing.Id, out _);
                await _inventory.GrantAsync(listing.Seller, listing.ItemId, listing.Count).ConfigureAwait(false);   // return escrow
                await Notify(listing.Seller, ReturnedEvent(listing)).ConfigureAwait(false);
            }
            await Reply(peer, cmd.CorrelationId, true, "", listing.Id, null);
        }

        private async Task SettleExpired()
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var listing in new List<Listing>(_listings.Values))
                if (now >= listing.ExpiresTicks) await Settle(listing).ConfigureAwait(false);
        }

        /// <summary>Finalizes a listing exactly once: pay seller + grant winner, or return the item if unsold.</summary>
        private async Task Settle(Listing listing)
        {
            if (!TryClaimSettle(listing)) return;
            _listings.TryRemove(listing.Id, out _);

            if (listing.CurrentBidder != null)
            {
                // Winner gets the escrowed item; seller gets the escrowed bid.
                await _inventory.GrantAsync(listing.CurrentBidder, listing.ItemId, listing.Count).ConfigureAwait(false);
                await _wallet.DepositAsync(listing.Seller, listing.Currency, listing.CurrentBid).ConfigureAwait(false);
                await Notify(listing.CurrentBidder, new AuctionEvent { Type = AuctionEventType.Won, ListingId = listing.Id, ItemId = listing.ItemId, Count = listing.Count, Amount = listing.CurrentBid, Currency = listing.Currency }).ConfigureAwait(false);
                await Notify(listing.Seller, new AuctionEvent { Type = AuctionEventType.Sold, ListingId = listing.Id, Amount = listing.CurrentBid, Currency = listing.Currency }).ConfigureAwait(false);
            }
            else
            {
                // No buyer — return the escrowed item to the seller.
                await _inventory.GrantAsync(listing.Seller, listing.ItemId, listing.Count).ConfigureAwait(false);
                await Notify(listing.Seller, ReturnedEvent(listing)).ConfigureAwait(false);
            }
        }

        private static bool TryClaimSettle(Listing listing)
        {
            lock (listing.Gate) { if (listing.Settled) return false; listing.Settled = true; return true; }
        }

        private static AuctionEvent ReturnedEvent(Listing l)
            => new AuctionEvent { Type = AuctionEventType.Returned, ListingId = l.Id, ItemId = l.ItemId, Count = l.Count, Currency = l.Currency };

        private List<AuctionListing> Snapshot()
        {
            var now = Stopwatch.GetTimestamp();
            var list = new List<AuctionListing>();
            foreach (var l in _listings.Values)
            {
                lock (l.Gate)
                {
                    if (l.Settled) continue;
                    list.Add(new AuctionListing
                    {
                        Id = l.Id, Seller = l.Seller, ItemId = l.ItemId, Count = l.Count, Currency = l.Currency,
                        MinBid = l.MinBid, Buyout = l.Buyout, CurrentBid = l.CurrentBid,
                        SecondsLeft = Math.Max(0, (l.ExpiresTicks - now) / Stopwatch.Frequency),
                    });
                }
            }
            return list;
        }

        private Task Notify(string playerKey, AuctionEvent evt)
        {
            var peer = _inventory.PeerFor(playerKey);
            if (peer == null) return Task.CompletedTask;
            try { return peer.SendAsync(AuctionTypes.Event, evt.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, string listingId, List<AuctionListing>? listings)
        {
            var reply = new AuctionReply { CorrelationId = corr, Success = ok, Error = error, ListingId = listingId, Listings = listings ?? new List<AuctionListing>() };
            try { return peer.SendAsync(AuctionTypes.Reply, reply.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }

        /// <summary>Stops the settlement timer.</summary>
        public void Dispose() => _timer.Dispose();
    }

    /// <summary>Auto-discovered server handler for auction commands.</summary>
    [MessageHandler(AuctionTypes.Command)]
    public sealed class AuctionCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = AuctionServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, AuctionCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated auction replies.</summary>
    [MessageHandler(AuctionTypes.Reply)]
    public sealed class AuctionReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = AuctionReply.Decode(data); AuctionRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for auction push events.</summary>
    [MessageHandler(AuctionTypes.Event)]
    public sealed class AuctionEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { AuctionRegistry.DispatchEvent(AuctionEvent.Decode(data)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the auction house to a server by composition.</summary>
    public static class AuctionServerExtensions
    {
        /// <summary>Enables the server-side auction house. Pass the <see cref="InventoryServer"/> and <see cref="WalletServer"/> from their <c>Use…</c> calls.</summary>
        public static AuctionServer UseAuction(this BaseServer server, InventoryServer inventory, WalletServer wallet)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            return AuctionServer.Enable(server, inventory, wallet);
        }
    }

    /// <summary>Attaches an auction driver to a client by composition.</summary>
    public static class AuctionClientExtensions
    {
        /// <summary>Enables client-side auctions; returns the driver (browse/sell/bid/buyout/cancel + events).</summary>
        public static AuctionClient UseAuction(this BaseClient client) => new AuctionClient(client);
    }

    /// <summary>One-time bootstrap so the auction handlers are discovered. Call at startup.</summary>
    public static class AuctionRuntime
    {
        /// <summary>Ensures the auction layer is discoverable.</summary>
        public static void Enable() { _ = AuctionTypes.Command; }
    }
}
