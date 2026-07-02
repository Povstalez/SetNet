using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Wallet;

namespace SetNet.Vendor
{
    /// <summary>Command operations (client → server) within the Vendor protocol channel.</summary>
    internal enum VendorOp : ushort { List = 1, Buy = 2, Sell = 3 }

    /// <summary>Thrown when a vendor operation fails (unknown vendor/item, out of stock, can't afford, timeout).</summary>
    public sealed class VendorException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public VendorException(string message) : base(message) { }
    }

    /// <summary>One item a vendor sells and/or buys back.</summary>
    public sealed class VendorEntry
    {
        /// <summary>The item id.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>Currency used for this entry (e.g. "gold").</summary>
        public string Currency { get; set; } = "gold";

        /// <summary>Cost per unit to buy from the vendor (0 = not for sale).</summary>
        public long BuyPrice { get; set; }

        /// <summary>Payout per unit when the player sells one back (0 = vendor won't buy it).</summary>
        public long SellPrice { get; set; }

        /// <summary>Units in stock (-1 = unlimited). Decremented on buy, not restored on sell.</summary>
        public long Stock { get; set; } = -1;

        /// <summary>Creates an empty entry (for serialization).</summary>
        public VendorEntry() { }

        /// <summary>Creates a vendor entry.</summary>
        public VendorEntry(string itemId, long buyPrice, long sellPrice = 0, string currency = "gold", long stock = -1)
        { ItemId = itemId; BuyPrice = buyPrice; SellPrice = sellPrice; Currency = currency; Stock = stock; }
    }

    internal sealed class VendorCatalog
    {
        public readonly ConcurrentDictionary<string, VendorEntry> Entries = new ConcurrentDictionary<string, VendorEntry>();
    }

    // ---- wire ----

    /// <summary>Body codecs for the Vendor channel (payload only; op/correlation are in the envelope).</summary>
    internal static class VendorCodec
    {
        public static byte[] EncodeCommand(string vendorId, string itemId, long count)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(vendorId ?? ""); w.Write(itemId ?? ""); w.Write(count);
            return ms.ToArray();
        }

        public static (string VendorId, string ItemId, long Count) DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return (r.ReadString(), r.ReadString(), r.ReadInt64());
        }

        public static byte[] EncodeReply(List<VendorEntry> entries)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(entries.Count);
            foreach (var e in entries) { w.Write(e.ItemId ?? ""); w.Write(e.Currency ?? ""); w.Write(e.BuyPrice); w.Write(e.SellPrice); w.Write(e.Stock); }
            return ms.ToArray();
        }

        public static List<VendorEntry> DecodeReply(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<VendorEntry>();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var count = r.ReadInt32();
            var entries = new List<VendorEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(new VendorEntry { ItemId = r.ReadString(), Currency = r.ReadString(), BuyPrice = r.ReadInt64(), SellPrice = r.ReadInt64(), Stock = r.ReadInt64() });
            return entries;
        }
    }

    /// <summary>
    /// Client-side vendor driver, attached by <see cref="VendorClientExtensions.UseVendor"/>. Browse an NPC shop's
    /// catalog and buy/sell items; the server debits/credits currency and moves items atomically. Inventory and
    /// wallet changes arrive through your <c>SetNet.Inventory</c> / <c>SetNet.Wallet</c> subscriptions.
    /// Rides the unified protocol on the <see cref="Channels.Vendor"/> channel.
    /// </summary>
    public sealed class VendorClient
    {
        private readonly BaseClient _client;

        internal VendorClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Lists a vendor's catalog (prices + stock).</summary>
        public Task<IReadOnlyList<VendorEntry>> ListAsync(string vendorId) => Send(VendorOp.List, vendorId, "", 0);

        /// <summary>Buys <paramref name="count"/> of an item from the vendor; throws <see cref="VendorException"/> if out of stock or unaffordable.</summary>
        public Task BuyAsync(string vendorId, string itemId, long count = 1) => Consume(VendorOp.Buy, vendorId, itemId, count);

        /// <summary>Sells <paramref name="count"/> of an item back to the vendor; throws <see cref="VendorException"/> if you lack them or the vendor won't buy.</summary>
        public Task SellAsync(string vendorId, string itemId, long count = 1) => Consume(VendorOp.Sell, vendorId, itemId, count);

        private async Task Consume(VendorOp op, string vendorId, string itemId, long count) { await Send(op, vendorId, itemId, Math.Max(1, count)).ConfigureAwait(false); }

        private async Task<IReadOnlyList<VendorEntry>> Send(VendorOp op, string vendorId, string itemId, long count)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Vendor, (ushort)op, VendorCodec.EncodeCommand(vendorId, itemId, count)).ConfigureAwait(false);
                return VendorCodec.DecodeReply(body);
            }
            catch (ProtocolException ex) { throw new VendorException(ex.Message); }
            catch (TimeoutException) { throw new VendorException("Vendor command timed out."); }
        }
    }

    /// <summary>
    /// Server-side vendor hub, attached by <see cref="VendorServerExtensions.UseVendor"/>. Holds NPC shop catalogs
    /// and settles buys/sells through the shared <see cref="WalletServer"/> and <see cref="InventoryServer"/>: a buy
    /// withdraws currency then grants the item and decrements stock; a sell revokes the item then pays out. Money is
    /// taken before goods are given (and vice-versa), so a failed step never leaves items/currency created.
    /// </summary>
    public sealed class VendorServer
    {
        private static readonly ConcurrentDictionary<BaseServer, VendorServer> Servers = new ConcurrentDictionary<BaseServer, VendorServer>();

        private readonly InventoryServer _inventory;
        private readonly WalletServer _wallet;
        private readonly ConcurrentDictionary<string, VendorCatalog> _vendors = new ConcurrentDictionary<string, VendorCatalog>();

        internal VendorServer(InventoryServer inventory, WalletServer wallet) { _inventory = inventory; _wallet = wallet; }

        internal static VendorServer Enable(BaseServer server, InventoryServer inventory, WalletServer wallet)
            => Servers.GetOrAdd(server, _ => new VendorServer(inventory, wallet));

        internal static VendorServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Registers (or replaces) a vendor's catalog.</summary>
        public VendorServer Define(string vendorId, IEnumerable<VendorEntry> entries)
        {
            if (string.IsNullOrEmpty(vendorId)) throw new ArgumentException("Vendor id required.", nameof(vendorId));
            var catalog = _vendors.GetOrAdd(vendorId, _ => new VendorCatalog());
            foreach (var e in entries) catalog.Entries[e.ItemId] = e;
            return this;
        }

        internal async Task HandleAsync(ChannelRequest request)
        {
            var (vendorId, itemId, count) = VendorCodec.DecodeCommand(request.RawBody);
            var me = _inventory.KeyOf(request.Peer);
            var op = (VendorOp)request.Op;

            if (!_vendors.TryGetValue(vendorId ?? "", out var catalog)) throw new ProtocolException("No such vendor.");

            if (op == VendorOp.List)
            {
                await request.ReplyRawAsync(VendorCodec.EncodeReply(new List<VendorEntry>(catalog.Entries.Values))).ConfigureAwait(false);
                return;
            }

            if (!catalog.Entries.TryGetValue(itemId ?? "", out var entry)) throw new ProtocolException("Vendor doesn't stock that item.");
            if (count < 1) throw new ProtocolException("Invalid quantity.");

            if (op == VendorOp.Buy)
            {
                if (entry.BuyPrice <= 0) throw new ProtocolException("Item is not for sale.");
                // Reserve stock atomically before charging.
                if (!TryTakeStock(entry, count)) throw new ProtocolException("Out of stock.");
                var total = entry.BuyPrice * count;
                if (!await _wallet.TryWithdrawAsync(me, entry.Currency, total).ConfigureAwait(false))
                {
                    ReturnStock(entry, count);   // refund the reservation
                    throw new ProtocolException($"Not enough {entry.Currency}.");
                }
                await _inventory.GrantAsync(me, entry.ItemId, count).ConfigureAwait(false);
                await request.ReplyRawAsync(Array.Empty<byte>()).ConfigureAwait(false);
            }
            else // Sell
            {
                if (entry.SellPrice <= 0) throw new ProtocolException("Vendor won't buy that.");
                if (!await _inventory.TryRevokeAsync(me, entry.ItemId, count).ConfigureAwait(false)) throw new ProtocolException("You don't have that many.");
                await _wallet.DepositAsync(me, entry.Currency, entry.SellPrice * count).ConfigureAwait(false);
                await request.ReplyRawAsync(Array.Empty<byte>()).ConfigureAwait(false);
            }
        }

        private static bool TryTakeStock(VendorEntry entry, long count)
        {
            if (entry.Stock < 0) return true;   // unlimited
            lock (entry)
            {
                if (entry.Stock < count) return false;
                entry.Stock -= count;
                return true;
            }
        }

        private static void ReturnStock(VendorEntry entry, long count)
        {
            if (entry.Stock < 0) return;
            lock (entry) entry.Stock += count;
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for vendor commands.</summary>
    [ProtocolChannel(Channels.Vendor)]
    public sealed class VendorChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = VendorServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("vendor is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the vendor hub to a server by composition.</summary>
    public static class VendorServerExtensions
    {
        /// <summary>Enables server-side vendors; returns the hub (define catalogs). Pass the <see cref="InventoryServer"/> and <see cref="WalletServer"/> from their <c>Use…</c> calls.</summary>
        public static VendorServer UseVendor(this BaseServer server, InventoryServer inventory, WalletServer wallet)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            return VendorServer.Enable(server, inventory, wallet);
        }
    }

    /// <summary>Attaches a vendor driver to a client by composition.</summary>
    public static class VendorClientExtensions
    {
        /// <summary>Enables client-side vendor interaction; returns the driver (<c>ListAsync</c>/<c>BuyAsync</c>/<c>SellAsync</c>).</summary>
        public static VendorClient UseVendor(this BaseClient client) => new VendorClient(client);
    }

    /// <summary>One-time bootstrap so the vendor channel service is discovered. Call at startup.</summary>
    public static class VendorRuntime
    {
        /// <summary>Ensures the vendor layer is discoverable.</summary>
        public static void Enable() { _ = typeof(VendorChannelService); }
    }
}
