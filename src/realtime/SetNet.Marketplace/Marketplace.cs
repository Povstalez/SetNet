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
using SetNet.Wallet;

namespace SetNet.Marketplace
{
    /// <summary>Reserved wire types for the marketplace service. Don't reuse these ids for application messages.</summary>
    public static class MarketplaceTypes
    {
        /// <summary>Client → server: post/cancel/book/my-orders command.</summary>
        public const ushort Command = ushort.MaxValue - 82;   // 65453

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 83;     // 65452

        /// <summary>Server → client: push event when one of your orders fills.</summary>
        public const ushort Event = ushort.MaxValue - 84;     // 65451
    }

    internal enum MarketOp : byte { PostBuy = 0, PostSell = 1, Cancel = 2, Book = 3, MyOrders = 4 }

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

    internal sealed class MarketCommand
    {
        public int CorrelationId;
        public MarketOp Op;
        public string OrderId = "";
        public string ItemId = "";
        public string Currency = "gold";
        public long Price;
        public long Quantity;

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write((byte)Op); w.Write(OrderId ?? ""); w.Write(ItemId ?? ""); w.Write(Currency ?? ""); w.Write(Price); w.Write(Quantity);
            return ms.ToArray();
        }

        public static MarketCommand Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new MarketCommand
            {
                CorrelationId = r.ReadInt32(), Op = (MarketOp)r.ReadByte(), OrderId = r.ReadString(), ItemId = r.ReadString(),
                Currency = r.ReadString(), Price = r.ReadInt64(), Quantity = r.ReadInt64(),
            };
        }
    }

    internal sealed class MarketReply
    {
        public int CorrelationId;
        public bool Success;
        public string Error = "";
        public string OrderId = "";
        public MarketBook Book = new MarketBook();
        public List<MarketOrder> Orders = new List<MarketOrder>();

        public byte[] Encode()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(CorrelationId); w.Write(Success); w.Write(Error ?? ""); w.Write(OrderId ?? "");
            WriteLevels(w, Book.Buys); WriteLevels(w, Book.Sells);
            w.Write(Orders.Count);
            foreach (var o in Orders) { w.Write(o.Id ?? ""); w.Write(o.ItemId ?? ""); w.Write(o.Currency ?? ""); w.Write((byte)o.Side); w.Write(o.Price); w.Write(o.Quantity); }
            return ms.ToArray();
        }

        public static MarketReply Decode(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var reply = new MarketReply { CorrelationId = r.ReadInt32(), Success = r.ReadBoolean(), Error = r.ReadString(), OrderId = r.ReadString() };
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

    internal static class MarketRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<MarketReply>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<MarketReply>>();
        private static readonly ConcurrentDictionary<MarketplaceClient, byte> Clients = new ConcurrentDictionary<MarketplaceClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<MarketReply> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, MarketReply reply) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(reply); }
        public static void RegisterClient(MarketplaceClient c) => Clients[c] = 0;
        public static void DispatchEvent(MarketFill fill) { foreach (var c in Clients.Keys) c.OnFill(fill); }
    }

    /// <summary>
    /// Client-side marketplace driver, attached by <see cref="MarketplaceClientExtensions.UseMarketplace"/>. Post
    /// limit buy/sell orders (items/currency escrowed immediately); crossing orders match instantly at the resting
    /// order's price, the rest rests on the book. Fills arrive via <see cref="Filled"/>. Unlike an auction, this is
    /// a continuous double-sided order book — trades happen the moment prices cross, not on a timer.
    /// </summary>
    public sealed class MarketplaceClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised each time one of your orders (partially) fills.</summary>
        public event Action<MarketFill>? Filled;

        internal MarketplaceClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            MarketRegistry.RegisterClient(this);
        }

        /// <summary>Posts a limit buy order (escrows <c>price × quantity</c> currency); returns the order id.</summary>
        public async Task<string> PostBuyAsync(string itemId, long quantity, long price, string currency = "gold")
            => (await Send(new MarketCommand { Op = MarketOp.PostBuy, ItemId = itemId, Quantity = quantity, Price = price, Currency = currency }).ConfigureAwait(false)).OrderId;

        /// <summary>Posts a limit sell order (escrows <paramref name="quantity"/> items); returns the order id.</summary>
        public async Task<string> PostSellAsync(string itemId, long quantity, long price, string currency = "gold")
            => (await Send(new MarketCommand { Op = MarketOp.PostSell, ItemId = itemId, Quantity = quantity, Price = price, Currency = currency }).ConfigureAwait(false)).OrderId;

        /// <summary>Cancels an open order and returns its remaining escrow.</summary>
        public Task CancelAsync(string orderId) => Send(new MarketCommand { Op = MarketOp.Cancel, OrderId = orderId });

        /// <summary>Fetches the aggregated order book for an item.</summary>
        public async Task<MarketBook> GetBookAsync(string itemId, string currency = "gold")
            => (await Send(new MarketCommand { Op = MarketOp.Book, ItemId = itemId, Currency = currency }).ConfigureAwait(false)).Book;

        /// <summary>Lists your open orders across all items.</summary>
        public async Task<IReadOnlyList<MarketOrder>> MyOrdersAsync()
            => (await Send(new MarketCommand { Op = MarketOp.MyOrders }).ConfigureAwait(false)).Orders;

        private async Task<MarketReply> Send(MarketCommand cmd)
        {
            var id = MarketRegistry.NextId();
            cmd.CorrelationId = id;
            var tcs = new TaskCompletionSource<MarketReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            MarketRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(MarketplaceTypes.Command, cmd.Encode(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    MarketReply reply;
                    try { reply = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new MarketplaceException("Marketplace command timed out."); }
                    if (!reply.Success) throw new MarketplaceException(reply.Error);
                    return reply;
                }
            }
            finally { MarketRegistry.Remove(id); }
        }

        internal void OnFill(MarketFill fill) => Filled?.Invoke(fill);
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

        private static string BookKey(string itemId, string currency) => (itemId ?? "") + "" + (currency ?? "gold");
        private Book GetBook(string itemId, string currency) => _books.GetOrAdd(BookKey(itemId, currency), _ => new Book());

        internal async Task OnCommand(BasePeer peer, MarketCommand cmd)
        {
            var me = _inventory.KeyOf(peer);
            try
            {
                switch (cmd.Op)
                {
                    case MarketOp.PostBuy: await Post(peer, me, cmd, isBuy: true); break;
                    case MarketOp.PostSell: await Post(peer, me, cmd, isBuy: false); break;
                    case MarketOp.Cancel: await Cancel(peer, me, cmd); break;
                    case MarketOp.Book: await BookReply(peer, cmd); break;
                    case MarketOp.MyOrders: await MyOrders(peer, me, cmd); break;
                }
            }
            catch (MarketplaceException ex) { await Reply(peer, cmd.CorrelationId, false, ex.Message, ""); }
        }

        private async Task Post(BasePeer peer, string me, MarketCommand cmd, bool isBuy)
        {
            var currency = string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency;
            if (string.IsNullOrEmpty(cmd.ItemId) || cmd.Quantity <= 0 || cmd.Price <= 0)
            { await Reply(peer, cmd.CorrelationId, false, "Invalid order.", ""); return; }

            // Escrow the full resource up front so resting quantity is always covered.
            if (isBuy)
            {
                if (!await _wallet.TryWithdrawAsync(me, currency, cmd.Price * cmd.Quantity).ConfigureAwait(false))
                { await Reply(peer, cmd.CorrelationId, false, $"Not enough {currency}.", ""); return; }
            }
            else
            {
                if (!await _inventory.TryRevokeAsync(me, cmd.ItemId, cmd.Quantity).ConfigureAwait(false))
                { await Reply(peer, cmd.CorrelationId, false, "You don't have that many.", ""); return; }
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

            await Reply(peer, cmd.CorrelationId, true, "", order.Id);
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

        private async Task Cancel(BasePeer peer, string me, MarketCommand cmd)
        {
            if (!_orderIndex.TryGetValue(cmd.OrderId ?? "", out var book)) throw new MarketplaceException("No such open order.");
            Order? removed = null;
            lock (book.Gate)
            {
                removed = book.Buys.FirstOrDefault(o => o.Id == cmd.OrderId) ?? book.Sells.FirstOrDefault(o => o.Id == cmd.OrderId);
                if (removed == null || removed.Owner != me) throw new MarketplaceException("Not your order.");
                (removed.IsBuy ? book.Buys : book.Sells).Remove(removed);
            }
            _orderIndex.TryRemove(removed.Id, out _);

            // Return the remaining escrow.
            if (removed.IsBuy) await _wallet.DepositAsync(me, removed.Currency, removed.Price * removed.Quantity).ConfigureAwait(false);
            else await _inventory.GrantAsync(me, removed.ItemId, removed.Quantity).ConfigureAwait(false);
            await Reply(peer, cmd.CorrelationId, true, "", removed.Id);
        }

        private Task BookReply(BasePeer peer, MarketCommand cmd)
        {
            var book = GetBook(cmd.ItemId, string.IsNullOrEmpty(cmd.Currency) ? "gold" : cmd.Currency);
            var view = new MarketBook();
            lock (book.Gate)
            {
                view.Buys = Aggregate(book.Buys, descending: true);
                view.Sells = Aggregate(book.Sells, descending: false);
            }
            return Reply(peer, cmd.CorrelationId, true, "", "", view);
        }

        private static List<MarketLevel> Aggregate(List<Order> side, bool descending)
        {
            var byPrice = new Dictionary<long, long>();
            foreach (var o in side) { byPrice.TryGetValue(o.Price, out var q); byPrice[o.Price] = q + o.Quantity; }
            var levels = byPrice.Select(kv => new MarketLevel { Price = kv.Key, Quantity = kv.Value });
            levels = descending ? levels.OrderByDescending(l => l.Price) : levels.OrderBy(l => l.Price);
            return levels.ToList();
        }

        private Task MyOrders(BasePeer peer, string me, MarketCommand cmd)
        {
            var mine = new List<MarketOrder>();
            foreach (var book in _books.Values)
                lock (book.Gate)
                    foreach (var o in book.Buys.Concat(book.Sells))
                        if (o.Owner == me) mine.Add(new MarketOrder { Id = o.Id, ItemId = o.ItemId, Currency = o.Currency, Side = o.IsBuy ? OrderSide.Buy : OrderSide.Sell, Price = o.Price, Quantity = o.Quantity });
            return Reply(peer, cmd.CorrelationId, true, "", "", orders: mine);
        }

        private Task Notify(string playerKey, MarketFill fill)
        {
            var peer = _inventory.PeerFor(playerKey);
            if (peer == null) return Task.CompletedTask;
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            { w.Write(fill.OrderId ?? ""); w.Write(fill.ItemId ?? ""); w.Write(fill.Currency ?? ""); w.Write((byte)fill.Side); w.Write(fill.Quantity); w.Write(fill.Price); }
            try { return peer.SendAsync(MarketplaceTypes.Event, ms.ToArray(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, string orderId, MarketBook? book = null, List<MarketOrder>? orders = null)
        {
            var reply = new MarketReply { CorrelationId = corr, Success = ok, Error = error, OrderId = orderId, Book = book ?? new MarketBook(), Orders = orders ?? new List<MarketOrder>() };
            try { return peer.SendAsync(MarketplaceTypes.Reply, reply.Encode(), DeliveryMethod.Reliable); } catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for marketplace commands.</summary>
    [MessageHandler(MarketplaceTypes.Command)]
    public sealed class MarketplaceCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = MarketplaceServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, MarketCommand.Decode(data)) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated marketplace replies.</summary>
    [MessageHandler(MarketplaceTypes.Reply)]
    public sealed class MarketplaceReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var r = MarketReply.Decode(data); MarketRegistry.Complete(r.CorrelationId, r); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for fill push events.</summary>
    [MessageHandler(MarketplaceTypes.Event)]
    public sealed class MarketplaceEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var fill = new MarketFill { OrderId = r.ReadString(), ItemId = r.ReadString(), Currency = r.ReadString(), Side = (OrderSide)r.ReadByte(), Quantity = r.ReadInt64(), Price = r.ReadInt64() };
            MarketRegistry.DispatchEvent(fill);
            return Task.CompletedTask;
        }
    }

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

    /// <summary>One-time bootstrap so the marketplace handlers are discovered. Call at startup.</summary>
    public static class MarketplaceRuntime
    {
        /// <summary>Ensures the marketplace layer is discoverable.</summary>
        public static void Enable() { _ = MarketplaceTypes.Command; }
    }
}
