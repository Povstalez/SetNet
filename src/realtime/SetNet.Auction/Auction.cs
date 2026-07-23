using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Wallet;

namespace SetNet.Auction
{
    /// <summary>Command operations (client → server) within the Auction protocol channel.</summary>
    internal enum AuctionOp : ushort { Browse = 1, Sell = 2, Bid = 3, Buyout = 4, Cancel = 5 }

    /// <summary>Push events (server → client) within the Auction protocol channel; values match <see cref="AuctionEventType"/>.</summary>
    internal enum AuctionEvt : ushort { Outbid = 10, Won = 11, Sold = 12, Returned = 13 }

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

    /// <summary>Decoded auction command body (the op and correlation live in the protocol envelope).</summary>
    internal sealed class AuctionCommand
    {
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
            w.Write(ListingId ?? ""); w.Write(ItemId ?? ""); w.Write(Count);
            w.Write(Currency ?? ""); w.Write(MinBid); w.Write(Buyout); w.Write(Amount); w.Write(DurationSeconds);
            return ms.ToArray();
        }

        public static AuctionCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new AuctionCommand
            {
                ListingId = r.ReadString(), ItemId = r.ReadString(), Count = r.ReadInt64(),
                Currency = r.ReadString(), MinBid = r.ReadInt64(), Buyout = r.ReadInt64(), Amount = r.ReadInt64(), DurationSeconds = r.ReadInt32(),
            };
        }
    }

    /// <summary>Decoded auction reply body (payload only; op/correlation are in the envelope).</summary>
    internal sealed class AuctionReply
    {
        public string ListingId = "";
        public List<AuctionListing> Listings = new List<AuctionListing>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ListingId ?? "");
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
            var reply = new AuctionReply();
            if (data == null || data.Length == 0) return reply;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            reply.ListingId = r.ReadString();
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
            w.Write(ListingId ?? ""); w.Write(ItemId ?? ""); w.Write(Count); w.Write(Amount); w.Write(Currency ?? "");
            return ms.ToArray();
        }

        internal static AuctionEvent Decode(AuctionEventType type, byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new AuctionEvent { Type = type, ListingId = r.ReadString(), ItemId = r.ReadString(), Count = r.ReadInt64(), Amount = r.ReadInt64(), Currency = r.ReadString() };
        }
    }

    /// <summary>
    /// Client-side auction driver, attached by <see cref="AuctionClientExtensions.UseAuction"/>. Browse listings,
    /// put items up for sale (escrowed from your inventory), bid or buy out (currency escrowed, prior bidder
    /// refunded), and cancel your own bid-free listings. Outcomes arrive via <see cref="Outbid"/>/<see cref="Won"/>/<see cref="Sold"/>/<see cref="Returned"/>.
    /// Rides the unified protocol on the <see cref="Channels.Auction"/> channel.
    /// </summary>
    public sealed class AuctionClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

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
            _subscriptions.Add(_client.OnRaw(Channels.Auction, (ushort)AuctionEvt.Outbid, b => OnEvent(AuctionEventType.Outbid, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Auction, (ushort)AuctionEvt.Won, b => OnEvent(AuctionEventType.Won, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Auction, (ushort)AuctionEvt.Sold, b => OnEvent(AuctionEventType.Sold, b)));
            _subscriptions.Add(_client.OnRaw(Channels.Auction, (ushort)AuctionEvt.Returned, b => OnEvent(AuctionEventType.Returned, b)));
        }

        /// <summary>Lists all active auctions.</summary>
        public async Task<IReadOnlyList<AuctionListing>> BrowseAsync()
            => (await Send(AuctionOp.Browse, new AuctionCommand()).ConfigureAwait(false)).Listings;

        /// <summary>Puts an item up for auction (escrowed from your inventory); returns the listing id.</summary>
        public async Task<string> SellAsync(string itemId, long count, long minBid, int durationSeconds, long buyout = 0, string currency = "gold")
        {
            var reply = await Send(AuctionOp.Sell, new AuctionCommand { ItemId = itemId, Count = count, MinBid = minBid, Buyout = buyout, Currency = currency, DurationSeconds = durationSeconds }).ConfigureAwait(false);
            return reply.ListingId;
        }

        /// <summary>Places a bid (must beat the current bid / meet the minimum); your currency is escrowed.</summary>
        public Task BidAsync(string listingId, long amount) => Send(AuctionOp.Bid, new AuctionCommand { ListingId = listingId, Amount = amount });

        /// <summary>Buys a listing outright at its buyout price and settles immediately.</summary>
        public Task BuyoutAsync(string listingId) => Send(AuctionOp.Buyout, new AuctionCommand { ListingId = listingId });

        /// <summary>Cancels your own listing (only allowed while it has no bids); the item is returned.</summary>
        public Task CancelAsync(string listingId) => Send(AuctionOp.Cancel, new AuctionCommand { ListingId = listingId });

        private async Task<AuctionReply> Send(AuctionOp op, AuctionCommand cmd)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Auction, (ushort)op, cmd.Encode()).ConfigureAwait(false);
                return AuctionReply.Decode(body);
            }
            catch (ProtocolException ex) { throw new AuctionException(ex.Message); }
            catch (TimeoutException) { throw new AuctionException("Auction command timed out."); }
        }

        private void OnEvent(AuctionEventType type, byte[] body)
        {
            var evt = AuctionEvent.Decode(type, body);
            switch (type)
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
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new AuctionServer(inventory, wallet);
                s.RegisterModule(new Registration(s, hub));   // stop the settlement timer + drop the static entry on server stop
                return hub;
            });

        internal static AuctionServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Releases a server's auction house (stops the settlement timer, drops the static registry entry) when the server is disposed/stopped.</summary>
        private sealed class Registration : IDisposable
        {
            private readonly BaseServer _server;
            private readonly AuctionServer _hub;
            public Registration(BaseServer server, AuctionServer hub) { _server = server; _hub = hub; }
            public void Dispose() { Servers.TryRemove(_server, out _); _hub.Dispose(); }
        }

        internal Task HandleAsync(ChannelRequest request)
        {
            var me = _inventory.KeyOf(request.Peer);
            var cmd = AuctionCommand.Decode(request.RawBody);
            switch ((AuctionOp)request.Op)
            {
                case AuctionOp.Browse: return request.ReplyRawAsync(new AuctionReply { Listings = Snapshot() }.Encode());
                case AuctionOp.Sell: return Sell(request, me, cmd);
                case AuctionOp.Bid: return Bid(request, me, cmd, cmd.Amount, buyout: false);
                case AuctionOp.Buyout: return Buyout(request, me, cmd);
                case AuctionOp.Cancel: return Cancel(request, me, cmd);
                default: return Task.CompletedTask;
            }
        }

        private async Task Sell(ChannelRequest request, string me, AuctionCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Count <= 0 || cmd.MinBid < 0 || cmd.DurationSeconds <= 0)
                throw new ProtocolException("Invalid listing.");

            // Escrow the item out of the seller's inventory.
            if (!await _inventory.TryRevokeAsync(me, cmd.ItemId, cmd.Count).ConfigureAwait(false))
                throw new ProtocolException("You don't have that item.");

            var listing = new Listing
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Seller = me, ItemId = cmd.ItemId, Count = cmd.Count, Currency = string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency,
                MinBid = cmd.MinBid, Buyout = cmd.Buyout,
                ExpiresTicks = Stopwatch.GetTimestamp() + (long)(cmd.DurationSeconds * (double)Stopwatch.Frequency),
            };
            _listings[listing.Id] = listing;
            await request.ReplyRawAsync(new AuctionReply { ListingId = listing.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task Bid(ChannelRequest request, string me, AuctionCommand cmd, long amount, bool buyout)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new ProtocolException("No such listing.");
            if (listing.Seller == me) throw new ProtocolException("You can't bid on your own listing.");

            string? refundBidder = null; long refundAmount = 0;
            lock (listing.Gate)
            {
                if (listing.Settled) throw new ProtocolException("Listing already ended.");
                var minRequired = listing.CurrentBidder == null ? listing.MinBid : listing.CurrentBid + 1;
                if (!buyout && amount < minRequired) throw new ProtocolException($"Bid must be at least {minRequired}.");
                refundBidder = listing.CurrentBidder;
                refundAmount = listing.CurrentBid;
            }

            // Escrow the new bid before committing it as the high bid.
            if (!await _wallet.TryWithdrawAsync(me, listing.Currency, amount).ConfigureAwait(false))
                throw new ProtocolException($"Not enough {listing.Currency}.");

            // Refund the previous high bidder (their escrow returns).
            if (refundBidder != null && refundAmount > 0)
            {
                await _wallet.DepositAsync(refundBidder, listing.Currency, refundAmount).ConfigureAwait(false);
                await Notify(refundBidder, new AuctionEvent { Type = AuctionEventType.Outbid, ListingId = listing.Id, Amount = refundAmount, Currency = listing.Currency }).ConfigureAwait(false);
            }

            lock (listing.Gate) { listing.CurrentBid = amount; listing.CurrentBidder = me; }

            if (buyout) await Settle(listing).ConfigureAwait(false);
            await request.ReplyRawAsync(new AuctionReply { ListingId = listing.Id }.Encode()).ConfigureAwait(false);
        }

        private async Task Buyout(ChannelRequest request, string me, AuctionCommand cmd)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new ProtocolException("No such listing.");
            if (listing.Buyout <= 0) throw new ProtocolException("This listing has no buyout.");
            await Bid(request, me, cmd, listing.Buyout, buyout: true).ConfigureAwait(false);
        }

        private async Task Cancel(ChannelRequest request, string me, AuctionCommand cmd)
        {
            if (!_listings.TryGetValue(cmd.ListingId ?? "", out var listing)) throw new ProtocolException("No such listing.");
            if (listing.Seller != me) throw new ProtocolException("Not your listing.");
            bool hasBid; lock (listing.Gate) { if (listing.Settled) throw new ProtocolException("Listing already ended."); hasBid = listing.CurrentBidder != null; }
            if (hasBid) throw new ProtocolException("Can't cancel a listing that has bids.");

            if (TryClaimSettle(listing))
            {
                _listings.TryRemove(listing.Id, out _);
                await _inventory.GrantAsync(listing.Seller, listing.ItemId, listing.Count).ConfigureAwait(false);   // return escrow
                await Notify(listing.Seller, ReturnedEvent(listing)).ConfigureAwait(false);
            }
            await request.ReplyRawAsync(new AuctionReply { ListingId = listing.Id }.Encode()).ConfigureAwait(false);
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
            try { return peer.PublishRawAsync(Channels.Auction, (ushort)EvtOp(evt.Type), evt.Encode()); } catch { return Task.CompletedTask; }
        }

        // AuctionEvt is AuctionEventType shifted into the event-op range (10..13).
        private static AuctionEvt EvtOp(AuctionEventType type) => (AuctionEvt)(10 + (byte)type);

        /// <summary>Stops the settlement timer.</summary>
        public void Dispose() => _timer.Dispose();
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for auction commands.</summary>
    [ProtocolChannel(Channels.Auction)]
    public sealed class AuctionChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = AuctionServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("auction is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

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

    /// <summary>One-time bootstrap so the auction channel service is discovered. Call at startup.</summary>
    public static class AuctionRuntime
    {
        /// <summary>Ensures the auction layer is discoverable.</summary>
        public static void Enable() { _ = typeof(AuctionChannelService); }
    }
}
