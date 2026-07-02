using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Inventory;
using SetNet.Wallet;

namespace SetNet.Vendor
{
    /// <summary>Reserved wire types for the vendor service. Don't reuse these ids for application messages.</summary>
    public static class VendorTypes
    {
        /// <summary>Client → server: list/buy/sell command.</summary>
        public const ushort Command = ushort.MaxValue - 61;   // 65474

        /// <summary>Server → client: correlated reply.</summary>
        public const ushort Reply = ushort.MaxValue - 62;     // 65473
    }

    internal enum VendorOp : byte { List = 0, Buy = 1, Sell = 2 }

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

    internal static class VendorCodec
    {
        public static byte[] EncodeCommand(int corr, VendorOp op, string vendorId, string itemId, long count)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write((byte)op); w.Write(vendorId ?? ""); w.Write(itemId ?? ""); w.Write(count);
            return ms.ToArray();
        }

        public static (int Corr, VendorOp Op, string VendorId, string ItemId, long Count) DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return (r.ReadInt32(), (VendorOp)r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadInt64());
        }

        public static byte[] EncodeReply(int corr, bool ok, string error, List<VendorEntry> entries)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(ok); w.Write(error ?? "");
            w.Write(entries.Count);
            foreach (var e in entries) { w.Write(e.ItemId ?? ""); w.Write(e.Currency ?? ""); w.Write(e.BuyPrice); w.Write(e.SellPrice); w.Write(e.Stock); }
            return ms.ToArray();
        }

        public static (int Corr, bool Ok, string Error, List<VendorEntry> Entries) DecodeReply(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32(); var ok = r.ReadBoolean(); var err = r.ReadString();
            var count = r.ReadInt32();
            var entries = new List<VendorEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(new VendorEntry { ItemId = r.ReadString(), Currency = r.ReadString(), BuyPrice = r.ReadInt64(), SellPrice = r.ReadInt64(), Stock = r.ReadInt64() });
            return (corr, ok, err, entries);
        }
    }

    internal static class VendorRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<(bool Ok, string Error, List<VendorEntry> Entries)>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<(bool, string, List<VendorEntry>)>>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<(bool, string, List<VendorEntry>)> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, (bool, string, List<VendorEntry>) r) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(r); }
    }

    /// <summary>
    /// Client-side vendor driver, attached by <see cref="VendorClientExtensions.UseVendor"/>. Browse an NPC shop's
    /// catalog and buy/sell items; the server debits/credits currency and moves items atomically. Inventory and
    /// wallet changes arrive through your <c>SetNet.Inventory</c> / <c>SetNet.Wallet</c> subscriptions.
    /// </summary>
    public sealed class VendorClient
    {
        private readonly BaseClient _client;

        internal VendorClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Lists a vendor's catalog (prices + stock).</summary>
        public async Task<IReadOnlyList<VendorEntry>> ListAsync(string vendorId)
        {
            var (_, _, entries) = await Send(VendorOp.List, vendorId, "", 0).ConfigureAwait(false);
            return entries;
        }

        /// <summary>Buys <paramref name="count"/> of an item from the vendor; throws <see cref="VendorException"/> if out of stock or unaffordable.</summary>
        public Task BuyAsync(string vendorId, string itemId, long count = 1) => Consume(VendorOp.Buy, vendorId, itemId, count);

        /// <summary>Sells <paramref name="count"/> of an item back to the vendor; throws <see cref="VendorException"/> if you lack them or the vendor won't buy.</summary>
        public Task SellAsync(string vendorId, string itemId, long count = 1) => Consume(VendorOp.Sell, vendorId, itemId, count);

        private async Task Consume(VendorOp op, string vendorId, string itemId, long count) { await Send(op, vendorId, itemId, Math.Max(1, count)).ConfigureAwait(false); }

        private async Task<(bool Ok, string Error, List<VendorEntry> Entries)> Send(VendorOp op, string vendorId, string itemId, long count)
        {
            var id = VendorRegistry.NextId();
            var tcs = new TaskCompletionSource<(bool, string, List<VendorEntry>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            VendorRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(VendorTypes.Command, VendorCodec.EncodeCommand(id, op, vendorId, itemId, count), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    (bool Ok, string Error, List<VendorEntry> Entries) result;
                    try { result = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new VendorException("Vendor command timed out."); }
                    if (!result.Ok) throw new VendorException(result.Error);
                    return result;
                }
            }
            finally { VendorRegistry.Remove(id); }
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

        internal async Task OnCommand(BasePeer peer, byte[] data)
        {
            var (corr, op, vendorId, itemId, count) = VendorCodec.DecodeCommand(data);
            var me = _inventory.KeyOf(peer);

            if (!_vendors.TryGetValue(vendorId ?? "", out var catalog)) { await Reply(peer, corr, false, "No such vendor.", null); return; }

            if (op == VendorOp.List)
            {
                await Reply(peer, corr, true, "", new List<VendorEntry>(catalog.Entries.Values)); return;
            }

            if (!catalog.Entries.TryGetValue(itemId ?? "", out var entry)) { await Reply(peer, corr, false, "Vendor doesn't stock that item.", null); return; }
            if (count < 1) { await Reply(peer, corr, false, "Invalid quantity.", null); return; }

            if (op == VendorOp.Buy)
            {
                if (entry.BuyPrice <= 0) { await Reply(peer, corr, false, "Item is not for sale.", null); return; }
                // Reserve stock atomically before charging.
                if (!TryTakeStock(entry, count)) { await Reply(peer, corr, false, "Out of stock.", null); return; }
                var total = entry.BuyPrice * count;
                if (!await _wallet.TryWithdrawAsync(me, entry.Currency, total).ConfigureAwait(false))
                {
                    ReturnStock(entry, count);   // refund the reservation
                    await Reply(peer, corr, false, $"Not enough {entry.Currency}.", null); return;
                }
                await _inventory.GrantAsync(me, entry.ItemId, count).ConfigureAwait(false);
                await Reply(peer, corr, true, "", null);
            }
            else // Sell
            {
                if (entry.SellPrice <= 0) { await Reply(peer, corr, false, "Vendor won't buy that.", null); return; }
                if (!await _inventory.TryRevokeAsync(me, entry.ItemId, count).ConfigureAwait(false)) { await Reply(peer, corr, false, "You don't have that many.", null); return; }
                await _wallet.DepositAsync(me, entry.Currency, entry.SellPrice * count).ConfigureAwait(false);
                await Reply(peer, corr, true, "", null);
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

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, List<VendorEntry>? entries)
        {
            try { return peer.SendAsync(VendorTypes.Reply, VendorCodec.EncodeReply(corr, ok, error, entries ?? new List<VendorEntry>()), DeliveryMethod.Reliable); }
            catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for vendor commands.</summary>
    [MessageHandler(VendorTypes.Command)]
    public sealed class VendorCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = VendorServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, data) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated vendor replies.</summary>
    [MessageHandler(VendorTypes.Reply)]
    public sealed class VendorReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, ok, err, entries) = VendorCodec.DecodeReply(data); VendorRegistry.Complete(corr, (ok, err, entries)); return Task.CompletedTask; }
    }

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

    /// <summary>One-time bootstrap so the vendor handlers are discovered. Call at startup.</summary>
    public static class VendorRuntime
    {
        /// <summary>Ensures the vendor layer is discoverable.</summary>
        public static void Enable() { _ = VendorTypes.Command; }
    }
}
