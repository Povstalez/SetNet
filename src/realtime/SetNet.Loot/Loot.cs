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

namespace SetNet.Loot
{
    /// <summary>Reserved wire types for the loot service. Don't reuse these ids for application messages.</summary>
    public static class LootTypes
    {
        /// <summary>Client → server: open-container command.</summary>
        public const ushort Command = ushort.MaxValue - 69;   // 65466

        /// <summary>Server → client: correlated reply (the rolled drops).</summary>
        public const ushort Reply = ushort.MaxValue - 70;     // 65465
    }

    /// <summary>Thrown when a loot open is denied or fails (unknown table, not permitted, timeout).</summary>
    public sealed class LootException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public LootException(string message) : base(message) { }
    }

    /// <summary>One possible drop in a loot table.</summary>
    public sealed class LootEntry
    {
        /// <summary>The item id to grant.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>How many of the item this entry grants when selected.</summary>
        public long Count { get; set; } = 1;

        /// <summary>Relative selection weight among the table's weighted entries (ignored when <see cref="Guaranteed"/>).</summary>
        public double Weight { get; set; } = 1;

        /// <summary>When true this entry always drops (once per roll), bypassing the weighted draw.</summary>
        public bool Guaranteed { get; set; }

        /// <summary>Creates an empty entry.</summary>
        public LootEntry() { }

        /// <summary>Creates a weighted entry.</summary>
        public LootEntry(string itemId, long count, double weight, bool guaranteed = false)
        { ItemId = itemId; Count = count; Weight = weight; Guaranteed = guaranteed; }
    }

    /// <summary>A loot table: guaranteed entries plus <see cref="Rolls"/> weighted draws.</summary>
    public sealed class LootTable
    {
        /// <summary>Unique table id.</summary>
        public string Id { get; set; } = "";

        /// <summary>How many weighted draws to perform per roll (guaranteed entries always drop regardless).</summary>
        public int Rolls { get; set; } = 1;

        /// <summary>The possible drops.</summary>
        public List<LootEntry> Entries { get; set; } = new List<LootEntry>();

        /// <summary>Creates an empty table.</summary>
        public LootTable() { }

        /// <summary>Creates a table with the given id, weighted-draw count, and entries.</summary>
        public LootTable(string id, int rolls, IEnumerable<LootEntry> entries)
        { Id = id; Rolls = rolls; Entries = new List<LootEntry>(entries); }
    }

    /// <summary>Settings for the loot service.</summary>
    public sealed class LootOptions
    {
        /// <summary>
        /// Authorizes a client-initiated open of a table (e.g. a loot box the player owns a key for). Default
        /// **denies** all client opens — server game logic should call <see cref="LootServer.RollAndGrantAsync"/>
        /// directly, or supply a policy that checks the player actually holds the container.
        /// </summary>
        public Func<string, string, bool> CanOpen { get; set; } = (_, __) => false;

        /// <summary>Optional fixed RNG seed for reproducible drops (tests). Null uses a time-seeded RNG.</summary>
        public int? Seed { get; set; }
    }

    // ---- wire ----

    internal static class LootCodec
    {
        public static byte[] EncodeCommand(int corr, string tableId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(tableId ?? "");
            return ms.ToArray();
        }

        public static (int Corr, string TableId) DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return (r.ReadInt32(), r.ReadString());
        }

        public static byte[] EncodeReply(int corr, bool ok, string error, List<ItemStack> drops)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(ok); w.Write(error ?? "");
            w.Write(drops.Count);
            foreach (var d in drops) { w.Write(d.ItemId ?? ""); w.Write(d.Count); }
            return ms.ToArray();
        }

        public static (int Corr, bool Ok, string Error, List<ItemStack> Drops) DecodeReply(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32(); var ok = r.ReadBoolean(); var err = r.ReadString();
            var count = r.ReadInt32();
            var drops = new List<ItemStack>(count);
            for (var i = 0; i < count; i++) drops.Add(new ItemStack(r.ReadString(), r.ReadInt64()));
            return (corr, ok, err, drops);
        }
    }

    internal static class LootRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<(bool Ok, string Error, List<ItemStack> Drops)>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<(bool, string, List<ItemStack>)>>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<(bool, string, List<ItemStack>)> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, (bool, string, List<ItemStack>) result) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(result); }
    }

    /// <summary>
    /// Client-side loot driver, attached by <see cref="LootClientExtensions.UseLoot"/>. Requests to open a container;
    /// the server checks the open is permitted (<see cref="LootOptions.CanOpen"/>), rolls the table, grants the drops
    /// to your inventory, and returns them for a reveal animation. Most loot is server-triggered, not client-opened.
    /// </summary>
    public sealed class LootClient
    {
        private readonly BaseClient _client;

        internal LootClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Opens a container by table id; returns the granted drops or throws <see cref="LootException"/> if denied.</summary>
        public async Task<IReadOnlyList<ItemStack>> OpenAsync(string tableId)
        {
            var id = LootRegistry.NextId();
            var tcs = new TaskCompletionSource<(bool, string, List<ItemStack>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            LootRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(LootTypes.Command, LootCodec.EncodeCommand(id, tableId), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    (bool Ok, string Error, List<ItemStack> Drops) result;
                    try { result = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new LootException("Loot open timed out."); }
                    if (!result.Ok) throw new LootException(result.Error);
                    return result.Drops;
                }
            }
            finally { LootRegistry.Remove(id); }
        }
    }

    /// <summary>
    /// Server-side loot hub, attached by <see cref="LootServerExtensions.UseLoot"/>. Holds weighted drop tables and
    /// rolls them server-side (clients never see the weights or RNG), granting results through the shared
    /// <see cref="InventoryServer"/>. Game logic rolls loot directly with <see cref="RollAndGrantAsync"/>; the
    /// client open path is gated by <see cref="LootOptions.CanOpen"/>.
    /// </summary>
    public sealed class LootServer
    {
        private static readonly ConcurrentDictionary<BaseServer, LootServer> Servers = new ConcurrentDictionary<BaseServer, LootServer>();

        private readonly InventoryServer _inventory;
        private readonly LootOptions _options;
        private readonly ConcurrentDictionary<string, LootTable> _tables = new ConcurrentDictionary<string, LootTable>();
        private readonly Random _rng;
        private readonly object _rngGate = new object();

        internal LootServer(InventoryServer inventory, LootOptions options)
        {
            _inventory = inventory;
            _options = options;
            _rng = options.Seed.HasValue ? new Random(options.Seed.Value) : new Random();
        }

        internal static LootServer Enable(BaseServer server, InventoryServer inventory, LootOptions options)
            => Servers.GetOrAdd(server, _ => new LootServer(inventory, options));

        internal static LootServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Registers (or replaces) a loot table.</summary>
        public LootServer Define(LootTable table)
        {
            if (table == null || string.IsNullOrEmpty(table.Id)) throw new ArgumentException("Loot table needs a non-empty id.", nameof(table));
            _tables[table.Id] = table;
            return this;
        }

        /// <summary>Rolls a table (guaranteed entries + weighted draws), merging identical items into stacks. No grant.</summary>
        public IReadOnlyList<ItemStack> Roll(string tableId)
        {
            if (!_tables.TryGetValue(tableId ?? "", out var table)) return Array.Empty<ItemStack>();

            var merged = new Dictionary<string, long>();
            void Add(string item, long count) { merged.TryGetValue(item, out var have); merged[item] = have + count; }

            var weighted = new List<LootEntry>();
            double totalWeight = 0;
            foreach (var e in table.Entries)
            {
                if (e.Guaranteed) Add(e.ItemId, e.Count);
                else if (e.Weight > 0) { weighted.Add(e); totalWeight += e.Weight; }
            }

            if (totalWeight > 0)
            {
                for (var i = 0; i < table.Rolls; i++)
                {
                    double pick;
                    lock (_rngGate) pick = _rng.NextDouble() * totalWeight;
                    foreach (var e in weighted)
                    {
                        pick -= e.Weight;
                        if (pick <= 0) { Add(e.ItemId, e.Count); break; }
                    }
                }
            }

            var drops = new List<ItemStack>(merged.Count);
            foreach (var kv in merged) drops.Add(new ItemStack(kv.Key, kv.Value));
            return drops;
        }

        /// <summary>Rolls a table and grants the drops to a player's inventory; returns what dropped.</summary>
        public async Task<IReadOnlyList<ItemStack>> RollAndGrantAsync(string playerKey, string tableId)
        {
            var drops = Roll(tableId);
            foreach (var d in drops) await _inventory.GrantAsync(playerKey, d.ItemId, d.Count).ConfigureAwait(false);
            return drops;
        }

        internal async Task OnCommand(BasePeer peer, byte[] data)
        {
            var (corr, tableId) = LootCodec.DecodeCommand(data);
            var playerKey = _inventory.KeyOf(peer);

            if (!_tables.ContainsKey(tableId ?? "")) { await Reply(peer, corr, false, "No such loot table.", new List<ItemStack>()).ConfigureAwait(false); return; }
            if (!_options.CanOpen(playerKey, tableId)) { await Reply(peer, corr, false, "Not permitted to open this.", new List<ItemStack>()).ConfigureAwait(false); return; }

            var drops = new List<ItemStack>(await RollAndGrantAsync(playerKey, tableId).ConfigureAwait(false));
            await Reply(peer, corr, true, "", drops).ConfigureAwait(false);
        }

        private static Task Reply(BasePeer peer, int corr, bool ok, string error, List<ItemStack> drops)
        {
            try { return peer.SendAsync(LootTypes.Reply, LootCodec.EncodeReply(corr, ok, error, drops), DeliveryMethod.Reliable); }
            catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Auto-discovered server handler for loot commands.</summary>
    [MessageHandler(LootTypes.Command)]
    public sealed class LootCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = LootServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, data) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated loot replies.</summary>
    [MessageHandler(LootTypes.Reply)]
    public sealed class LootReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, ok, err, drops) = LootCodec.DecodeReply(data); LootRegistry.Complete(corr, (ok, err, drops)); return Task.CompletedTask; }
    }

    /// <summary>Attaches the loot hub to a server by composition.</summary>
    public static class LootServerExtensions
    {
        /// <summary>Enables server-side loot; returns the hub (register tables, roll+grant). Pass the <see cref="InventoryServer"/> from <c>UseInventory</c>.</summary>
        public static LootServer UseLoot(this BaseServer server, InventoryServer inventory, LootOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            return LootServer.Enable(server, inventory, options ?? new LootOptions());
        }
    }

    /// <summary>Attaches a loot driver to a client by composition.</summary>
    public static class LootClientExtensions
    {
        /// <summary>Enables client-side loot opening; returns the driver (<c>OpenAsync</c>).</summary>
        public static LootClient UseLoot(this BaseClient client) => new LootClient(client);
    }

    /// <summary>One-time bootstrap so the loot handlers are discovered. Call at startup.</summary>
    public static class LootRuntime
    {
        /// <summary>Ensures the loot layer is discoverable.</summary>
        public static void Enable() { _ = LootTypes.Command; }
    }
}
