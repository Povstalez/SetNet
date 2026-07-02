using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Wallet;

namespace SetNet.Marketplace
{
    /// <summary>Command operations (client → server) within the Marketplace protocol channel.</summary>
    internal enum MarketOp : ushort { PostBuy = 1, PostSell = 2, Cancel = 3, Book = 4, MyOrders = 5 }

    /// <summary>Push events (server → client) within the Marketplace protocol channel.</summary>
    internal enum MarketEvt : ushort { Filled = 10 }

    /// <summary>Thrown when a marketplace operation fails (insufficient funds/items, unknown order, timeout).</summary>
    public sealed class MarketplaceException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public MarketplaceException(string message) : base(message) { }
    }

    /// <summary>Which side of the book an order is on.</summary>
    public enum OrderSide : byte
    {
        /// <summary>A bid: wants to buy items with currency.</summary>
        Buy = 0,

        /// <summary>An ask: wants to sell items for currency.</summary>
        Sell = 1,
    }

    /// <summary>A resting order as seen by its owner.</summary>
    public sealed class MarketOrder
    {
        /// <summary>Order id.</summary>
        public string Id { get; set; } = "";

        /// <summary>Item traded.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>Currency.</summary>
        public string Currency { get; set; } = "gold";

        /// <summary>Buy or sell.</summary>
        public OrderSide Side { get; set; }

        /// <summary>Limit price per unit.</summary>
        public long Price { get; set; }

        /// <summary>Quantity still open (unfilled).</summary>
        public long Quantity { get; set; }
    }

    /// <summary>An aggregated price level in the order book.</summary>
    public sealed class MarketLevel
    {
        /// <summary>Price per unit.</summary>
        public long Price { get; set; }

        /// <summary>Total quantity resting at this price.</summary>
        public long Quantity { get; set; }
    }

    /// <summary>A snapshot of one item's order book, best prices first.</summary>
    public sealed class MarketBook
    {
        /// <summary>Bids, highest price first.</summary>
        public List<MarketLevel> Buys { get; set; } = new List<MarketLevel>();

        /// <summary>Asks, lowest price first.</summary>
        public List<MarketLevel> Sells { get; set; } = new List<MarketLevel>();
    }

    /// <summary>A fill notification pushed to an order's owner.</summary>
    public sealed class MarketFill
    {
        /// <summary>Your order that (partially) filled.</summary>
        public string OrderId { get; internal set; } = "";

        /// <summary>Item traded.</summary>
        public string ItemId { get; internal set; } = "";

        /// <summary>Currency.</summary>
        public string Currency { get; internal set; } = "gold";

        /// <summary>Your order's side.</summary>
        public OrderSide Side { get; internal set; }

        /// <summary>Quantity filled in this trade.</summary>
        public long Quantity { get; internal set; }

        /// <summary>Price the trade executed at (the resting order's price).</summary>
        public long Price { get; internal set; }

        internal byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(OrderId ?? ""); w.Write(ItemId ?? ""); w.Write(Currency ?? ""); w.Write((byte)Side); w.Write(Quantity); w.Write(Price);
            return ms.ToArray();
        }

        internal static MarketFill Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new MarketFill { OrderId = r.ReadString(), ItemId = r.ReadString(), Currency = r.ReadString(), Side = (OrderSide)r.ReadByte(), Quantity = r.ReadInt64(), Price = r.ReadInt64() };
        }
    }

    // ---- internal order book ----

    internal sealed class Order
    {
        public string Id = "";
        public string Owner = "";
        public string ItemId = "";
        public string Currency = "gold";
        public bool IsBuy;
        public long Price;
        public long Quantity;
        public long Seq;
    }

    internal sealed class Book
    {
        public readonly object Gate = new object();
        public readonly List<Order> Buys = new List<Order>();
        public readonly List<Order> Sells = new List<Order>();
    }

    internal readonly struct Fill
    {
        public readonly string Buyer, Seller, ItemId, Currency, BuyOrderId, SellOrderId;
        public readonly long Quantity, TradePrice, BuyerLimit;
        public Fill(string buyer, string seller, string itemId, string currency, string buyOrderId, string sellOrderId, long qty, long tradePrice, long buyerLimit)
        { Buyer = buyer; Seller = seller; ItemId = itemId; Currency = currency; BuyOrderId = buyOrderId; SellOrderId = sellOrderId; Quantity = qty; TradePrice = tradePrice; BuyerLimit = buyerLimit; }
    }

    // ---- wire ----

    /// <summary>Decoded marketplace command body (the op and correlation live in the protocol envelope).</summary>
    internal sealed class MarketCommand
    {
        public string OrderId = "";
        public string ItemId = "";
        public string Currency = "gold";
        public long Price;
        public long Quantity;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(OrderId ?? ""); w.Write(ItemId ?? ""); w.Write(Currency ?? ""); w.Write(Price); w.Write(Quantity);
            return ms.ToArray();
        }

        public static MarketCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new MarketCommand
            {
                OrderId = r.ReadString(), ItemId = r.ReadString(),
                Currency = r.ReadString(), Price = r.ReadInt64(), Quantity = r.ReadInt64(),
            };
        }
    }

    /// <summary>Decoded marketplace reply body (payload only; op/correlation are in the envelope).</summary>
    internal sealed class MarketReply
    {
        public string OrderId = "";
        public MarketBook Book = new MarketBook();
        public List<MarketOrder> Orders = new List<MarketOrder>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(OrderId ?? "");
            WriteLevels(w, Book.Buys); WriteLevels(w, Book.Sells);
            w.Write(Orders.Count);
            foreach (var o in Orders) { w.Write(o.Id ?? ""); w.Write(o.ItemId ?? ""); w.Write(o.Currency ?? ""); w.Write((byte)o.Side); w.Write(o.Price); w.Write(o.Quantity); }
            return ms.ToArray();
        }

        public static MarketReply Decode(byte[] data)
        {
            var reply = new MarketReply();
            if (data == null || data.Length == 0) return reply;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            reply.OrderId = r.ReadString();
            reply.Book.Buys = ReadLevels(r); reply.Book.Sells = ReadLevels(r);
            var oc = r.ReadInt32();
            for (var i = 0; i < oc; i++) reply.Orders.Add(new MarketOrder { Id = r.ReadString(), ItemId = r.ReadString(), Currency = r.ReadString(), Side = (OrderSide)r.ReadByte(), Price = r.ReadInt64(), Quantity = r.ReadInt64() });
            return reply;
        }

        private static void WriteLevels(BinaryWriter w, List<MarketLevel> levels)
        {
            w.Write(levels.Count);
            foreach (var l in levels) { w.Write(l.Price); w.Write(l.Quantity); }
        }

        private static List<MarketLevel> ReadLevels(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<MarketLevel>(count);
            for (var i = 0; i < count; i++) list.Add(new MarketLevel { Price = r.ReadInt64(), Quantity = r.ReadInt64() });
            return list;
        }
    }

    /// <summary>
    /// Client-side marketplace driver, attached by <see cref="MarketplaceClientExtensions.UseMarketplace"/>. Post
    /// limit buy/sell orders (items/currency escrowed immediately); crossing orders match instantly at the resting
    /// order's price, the rest rests on the book. Fills arrive via <see cref="Filled"/>. Unlike an auction, this is
    /// a continuous double-sided order book — trades happen the moment prices cross, not on a timer.
    /// Rides the unified protocol on the <see cref="Channels.Marketplace"/> channel.
    /// </summary>
    public sealed class MarketplaceClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised each time one of your orders (partially) fills.</summary>
        public event Action<MarketFill>? Filled;

        internal MarketplaceClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.Marketplace, (ushort)MarketEvt.Filled, b => Filled?.Invoke(MarketFill.Decode(b))));
        }

        /// <summary>Posts a limit buy order (escrows <c>price × quantity</c> currency); returns the order id.</summary>
        public async Task<string> PostBuyAsync(string itemId, long quantity, long price, string currency = "gold")
            => (await Send(MarketOp.PostBuy, new MarketCommand { ItemId = itemId, Quantity = quantity, Price = price, Currency = currency }).ConfigureAwait(false)).OrderId;

        /// <summary>Posts a limit sell order (escrows <paramref name="quantity"/> items); returns the order id.</summary>
        public async Task<string> PostSellAsync(string itemId, long quantity, long price, string currency = "gold")
            => (await Send(MarketOp.PostSell, new MarketCommand { ItemId = itemId, Quantity = quantity, Price = price, Currency = currency }).ConfigureAwait(false)).OrderId;

        /// <summary>Cancels an open order and returns its remaining escrow.</summary>
        public Task CancelAsync(string orderId) => Send(MarketOp.Cancel, new MarketCommand { OrderId = orderId });

        /// <summary>Fetches the aggregated order book for an item.</summary>
        public async Task<MarketBook> GetBookAsync(string itemId, string currency = "gold")
            => (await Send(MarketOp.Book, new MarketCommand { ItemId = itemId, Currency = currency }).ConfigureAwait(false)).Book;

        /// <summary>Lists your open orders across all items.</summary>
        public async Task<IReadOnlyList<MarketOrder>> MyOrdersAsync()
            => (await Send(MarketOp.MyOrders, new MarketCommand()).ConfigureAwait(false)).Orders;

        private async Task<MarketReply> Send(MarketOp op, MarketCommand cmd)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Marketplace, (ushort)op, cmd.Encode()).ConfigureAwait(false);
                return MarketReply.Decode(body);
            }
            catch (ProtocolException ex) { throw new MarketplaceException(ex.Message); }
            catch (TimeoutException) { throw new MarketplaceException("Marketplace command timed out."); }
        }
    }

    /// <summary>
    /// Server-side marketplace, attached by <see cref="MarketplaceServerExtensions.UseMarketplace"/>. Runs a
    /// continuous double-sided order book per item with price-time priority. Resources are escrowed on post
    /// (currency for buys, items for sells) through the shared <see cref="WalletServer"/> / <see cref="InventoryServer"/>;
    /// crossing orders trade at the <b>resting</b> order's price, so a marketable order can only improve on its limit
    /// and the buyer is refunded the difference. Matching is decided under a per-book lock; the resulting item/
    /// currency moves are applied afterward, so escrow can never dupe or vanish.
    /// </summary>
    public sealed class MarketplaceServer
    {
        private static readonly ConcurrentDictionary<BaseServer, MarketplaceServer> Servers = new ConcurrentDictionary<BaseServer, MarketplaceServer>();

        private readonly InventoryServer _inventory;
        private readonly WalletServer _wallet;
        private readonly ConcurrentDictionary<string, Book> _books = new ConcurrentDictionary<string, Book>();
        private readonly ConcurrentDictionary<string, Book> _orderIndex = new ConcurrentDictionary<string, Book>();   // orderId -> its book
        private long _seq;

        internal MarketplaceServer(InventoryServer inventory, WalletServer wallet) { _inventory = inventory; _wallet = wallet; }

        internal static MarketplaceServer Enable(BaseServer server, InventoryServer inventory, WalletServer wallet)
            => Servers.GetOrAdd(server, _ => new MarketplaceServer(inventory, wallet));

        internal static MarketplaceServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        private static string BookKey(string itemId, string currency) => (itemId ?? "") + "" + (currency ?? "gold");
        private Book GetBook(string itemId, string currency) => _books.GetOrAdd(BookKey(itemId, currency), _ => new Book());

        internal Task HandleAsync(ChannelRequest request)
        {
            var me = _inventory.KeyOf(request.Peer);
            var cmd = MarketCommand.Decode(request.RawBody);
            switch ((MarketOp)request.Op)
            {
                case MarketOp.PostBuy: return Post(request, me, cmd, isBuy: true);
                case MarketOp.PostSell: return Post(request, me, cmd, isBuy: false);
                case MarketOp.Cancel: return Cancel(request, me, cmd);
                case MarketOp.Book: return BookReply(request, cmd);
                case MarketOp.MyOrders: return MyOrders(request, me);
                default: return Task.CompletedTask;
            }
        }

        private async Task Post(ChannelRequest request, string me, MarketCommand cmd, bool isBuy)
        {
            var currency = string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency;
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Quantity <= 0 || cmd.Price <= 0)
                throw new ProtocolException("Invalid order.");

            // Escrow the full resource up front so resting quantity is always covered.
            if (isBuy)
            {
                if (!await _wallet.TryWithdrawAsync(me, currency, cmd.Price * cmd.Quantity).ConfigureAwait(false))
                    throw new ProtocolException($"Not enough {currency}.");
            }
            else
            {
                if (!await _inventory.TryRevokeAsync(me, cmd.ItemId, cmd.Quantity).ConfigureAwait(false))
                    throw new ProtocolException("You don't have that many.");
            }

            var order = new Order
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12), Owner = me, ItemId = cmd.ItemId, Currency = currency,
                IsBuy = isBuy, Price = cmd.Price, Quantity = cmd.Quantity, Seq = Interlocked.Increment(ref _seq),
            };
            var book = GetBook(cmd.ItemId, currency);

            List<Fill> fills;
            bool rests;
            lock (book.Gate) fills = Match(book, order, out rests);
            if (rests) _orderIndex[order.Id] = book;

            await request.ReplyRawAsync(new MarketReply { OrderId = order.Id }.Encode()).ConfigureAwait(false);
            foreach (var fill in fills) await Execute(fill).ConfigureAwait(false);
        }

        /// <summary>Matches an incoming (already-escrowed) order against the opposite side; returns the fills produced.</summary>
        private List<Fill> Match(Book book, Order incoming, out bool rests)
        {
            var fills = new List<Fill>();
            var opposite = incoming.IsBuy ? book.Sells : book.Buys;

            while (incoming.Quantity > 0)
            {
                var best = BestCrossing(opposite, incoming);
                if (best == null) break;

                var qty = Math.Min(incoming.Quantity, best.Quantity);
                var tradePrice = best.Price;   // price-time priority: the resting order sets the price
                var buyer = incoming.IsBuy ? incoming.Owner : best.Owner;
                var seller = incoming.IsBuy ? best.Owner : incoming.Owner;
                var buyOrderId = incoming.IsBuy ? incoming.Id : best.Id;
                var sellOrderId = incoming.IsBuy ? best.Id : incoming.Id;
                var buyerLimit = incoming.IsBuy ? incoming.Price : best.Price;

                fills.Add(new Fill(buyer, seller, incoming.ItemId, incoming.Currency, buyOrderId, sellOrderId, qty, tradePrice, buyerLimit));

                incoming.Quantity -= qty;
                best.Quantity -= qty;
                if (best.Quantity == 0) { opposite.Remove(best); _orderIndex.TryRemove(best.Id, out _); }
            }

            rests = incoming.Quantity > 0;
            if (rests) (incoming.IsBuy ? book.Buys : book.Sells).Add(incoming);
            return fills;
        }

        private static Order? BestCrossing(List<Order> resting, Order incoming)
        {
            Order? best = null;
            foreach (var o in resting)
            {
                var crosses = incoming.IsBuy ? o.Price <= incoming.Price : o.Price >= incoming.Price;
                if (!crosses) continue;
                if (best == null) { best = o; continue; }
                // Best = most aggressive price, tie-broken by earliest sequence.
                var better = incoming.IsBuy ? (o.Price < best.Price || (o.Price == best.Price && o.Seq < best.Seq))
                                            : (o.Price > best.Price || (o.Price == best.Price && o.Seq < best.Seq));
                if (better) best = o;
            }
            return best;
        }

        /// <summary>Applies one fill's item/currency moves: buyer gets items, seller gets proceeds, buyer refunded the price improvement.</summary>
        private async Task Execute(Fill fill)
        {
            await _inventory.GrantAsync(fill.Buyer, fill.ItemId, fill.Quantity).ConfigureAwait(false);
            await _wallet.DepositAsync(fill.Seller, fill.Currency, fill.TradePrice * fill.Quantity).ConfigureAwait(false);
            var refund = (fill.BuyerLimit - fill.TradePrice) * fill.Quantity;
            if (refund > 0) await _wallet.DepositAsync(fill.Buyer, fill.Currency, refund).ConfigureAwait(false);

            await Notify(fill.Buyer, new MarketFill { OrderId = fill.BuyOrderId, ItemId = fill.ItemId, Currency = fill.Currency, Side = OrderSide.Buy, Quantity = fill.Quantity, Price = fill.TradePrice }).ConfigureAwait(false);
            await Notify(fill.Seller, new MarketFill { OrderId = fill.SellOrderId, ItemId = fill.ItemId, Currency = fill.Currency, Side = OrderSide.Sell, Quantity = fill.Quantity, Price = fill.TradePrice }).ConfigureAwait(false);
        }

        private async Task Cancel(ChannelRequest request, string me, MarketCommand cmd)
        {
            if (!_orderIndex.TryGetValue(cmd.OrderId ?? "", out var book)) throw new ProtocolException("No such open order.");
            Order? removed = null;
            lock (book.Gate)
            {
                removed = book.Buys.FirstOrDefault(o => o.Id == cmd.OrderId) ?? book.Sells.FirstOrDefault(o => o.Id == cmd.OrderId);
                if (removed == null || removed.Owner != me) throw new ProtocolException("Not your order.");
                (removed.IsBuy ? book.Buys : book.Sells).Remove(removed);
            }
            _orderIndex.TryRemove(removed.Id, out _);

            // Return the remaining escrow.
            if (removed.IsBuy) await _wallet.DepositAsync(me, removed.Currency, removed.Price * removed.Quantity).ConfigureAwait(false);
            else await _inventory.GrantAsync(me, removed.ItemId, removed.Quantity).ConfigureAwait(false);
            await request.ReplyRawAsync(new MarketReply { OrderId = removed.Id }.Encode()).ConfigureAwait(false);
        }

        private Task BookReply(ChannelRequest request, MarketCommand cmd)
        {
            var book = GetBook(cmd.ItemId, string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency);
            var view = new MarketBook();
            lock (book.Gate)
            {
                view.Buys = Aggregate(book.Buys, descending: true);
                view.Sells = Aggregate(book.Sells, descending: false);
            }
            return request.ReplyRawAsync(new MarketReply { Book = view }.Encode());
        }

        private static List<MarketLevel> Aggregate(List<Order> side, bool descending)
        {
            var byPrice = new Dictionary<long, long>();
            foreach (var o in side) { byPrice.TryGetValue(o.Price, out var q); byPrice[o.Price] = q + o.Quantity; }
            var levels = byPrice.Select(kv => new MarketLevel { Price = kv.Key, Quantity = kv.Value });
            levels = descending ? levels.OrderByDescending(l => l.Price) : levels.OrderBy(l => l.Price);
            return levels.ToList();
        }

        private Task MyOrders(ChannelRequest request, string me)
        {
            var mine = new List<MarketOrder>();
            foreach (var book in _books.Values)
                lock (book.Gate)
                    foreach (var o in book.Buys.Concat(book.Sells))
                        if (o.Owner == me) mine.Add(new MarketOrder { Id = o.Id, ItemId = o.ItemId, Currency = o.Currency, Side = o.IsBuy ? OrderSide.Buy : OrderSide.Sell, Price = o.Price, Quantity = o.Quantity });
            return request.ReplyRawAsync(new MarketReply { Orders = mine }.Encode());
        }

        private Task Notify(string playerKey, MarketFill fill)
        {
            var peer = _inventory.PeerFor(playerKey);
            if (peer == null) return Task.CompletedTask;
            try { return peer.PublishRawAsync(Channels.Marketplace, (ushort)MarketEvt.Filled, fill.Encode()); } catch { return Task.CompletedTask; }
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for marketplace commands.</summary>
    [ProtocolChannel(Channels.Marketplace)]
    public sealed class MarketplaceChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = MarketplaceServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("marketplace is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the marketplace to a server by composition.</summary>
    public static class MarketplaceServerExtensions
    {
        /// <summary>Enables the server-side marketplace. Pass the <see cref="InventoryServer"/> and <see cref="WalletServer"/> from their <c>Use…</c> calls.</summary>
        public static MarketplaceServer UseMarketplace(this BaseServer server, InventoryServer inventory, WalletServer wallet)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            return MarketplaceServer.Enable(server, inventory, wallet);
        }
    }

    /// <summary>Attaches a marketplace driver to a client by composition.</summary>
    public static class MarketplaceClientExtensions
    {
        /// <summary>Enables client-side marketplace; returns the driver (post/cancel/book/my-orders + <c>Filled</c>).</summary>
        public static MarketplaceClient UseMarketplace(this BaseClient client) => new MarketplaceClient(client);
    }

    /// <summary>One-time bootstrap so the marketplace channel service is discovered. Call at startup.</summary>
    public static class MarketplaceRuntime
    {
        /// <summary>Ensures the marketplace layer is discoverable.</summary>
        public static void Enable() { _ = typeof(MarketplaceChannelService); }
    }
}
