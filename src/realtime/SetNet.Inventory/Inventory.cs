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

namespace SetNet.Inventory
{
    /// <summary>Reserved wire types for the inventory service. Don't reuse these ids for application messages.</summary>
    public static class InventoryTypes
    {
        /// <summary>Client → server: query command.</summary>
        public const ushort Command = ushort.MaxValue - 46;   // 65489

        /// <summary>Server → client: correlated reply (an inventory snapshot).</summary>
        public const ushort Reply = ushort.MaxValue - 47;     // 65488

        /// <summary>Server → client: push event when the peer's inventory changes.</summary>
        public const ushort Event = ushort.MaxValue - 48;     // 65487
    }

    /// <summary>Thrown when an inventory operation fails (query timeout).</summary>
    public sealed class InventoryException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public InventoryException(string message) : base(message) { }
    }

    /// <summary>
    /// One stackable line in a player's inventory: a quantity of an item id. Per-instance data is out of scope —
    /// encode it into <see cref="ItemId"/> (e.g. <c>"sword#uuid"</c>) if you need non-stacking instances.
    /// </summary>
    public sealed class ItemStack
    {
        /// <summary>The item's application-defined identifier.</summary>
        public string ItemId { get; set; } = "";

        /// <summary>How many of the item the player holds (always &gt; 0 in a stored stack).</summary>
        public long Count { get; set; }

        /// <summary>Creates an empty stack (for serialization).</summary>
        public ItemStack() { }

        /// <summary>Creates a stack of <paramref name="count"/> × <paramref name="itemId"/>.</summary>
        public ItemStack(string itemId, long count) { ItemId = itemId; Count = count; }
    }

    // ---- store ----

    /// <summary>
    /// Persistence for player inventories. The default is <see cref="MemoryInventoryStore"/> (in-process); supply a
    /// Redis/DB-backed store for durability and cross-node sharing. Implementations must be safe for concurrent calls
    /// per player key and must apply grants/revokes atomically (<see cref="TryRevokeAsync"/> is the atomic
    /// take-if-enough primitive that trades and mail claims rely on).
    /// </summary>
    public interface IInventoryStore
    {
        /// <summary>Returns the player's current stacks (empty when the player is unknown).</summary>
        Task<IReadOnlyList<ItemStack>> GetAsync(string playerKey);

        /// <summary>Adds <paramref name="count"/> of <paramref name="itemId"/> to the player's inventory (creating the stack if absent).</summary>
        Task GrantAsync(string playerKey, string itemId, long count);

        /// <summary>Atomically removes <paramref name="count"/> of <paramref name="itemId"/>; returns false (and changes nothing) if the player holds fewer.</summary>
        Task<bool> TryRevokeAsync(string playerKey, string itemId, long count);
    }

    /// <summary>In-process inventory store. Fine for a single node / tests; swap for a shared store to persist or cluster.</summary>
    public sealed class MemoryInventoryStore : IInventoryStore
    {
        private readonly ConcurrentDictionary<string, Dictionary<string, long>> _players = new ConcurrentDictionary<string, Dictionary<string, long>>();

        /// <inheritdoc/>
        public Task<IReadOnlyList<ItemStack>> GetAsync(string playerKey)
        {
            var bag = _players.GetOrAdd(playerKey ?? "", _ => new Dictionary<string, long>());
            List<ItemStack> stacks;
            lock (bag)
            {
                stacks = new List<ItemStack>(bag.Count);
                foreach (var kv in bag) if (kv.Value > 0) stacks.Add(new ItemStack(kv.Key, kv.Value));
            }
            return Task.FromResult<IReadOnlyList<ItemStack>>(stacks);
        }

        /// <inheritdoc/>
        public Task GrantAsync(string playerKey, string itemId, long count)
        {
            if (count <= 0) return Task.CompletedTask;
            var bag = _players.GetOrAdd(playerKey ?? "", _ => new Dictionary<string, long>());
            lock (bag) { bag.TryGetValue(itemId, out var have); bag[itemId] = have + count; }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<bool> TryRevokeAsync(string playerKey, string itemId, long count)
        {
            if (count <= 0) return Task.FromResult(true);
            var bag = _players.GetOrAdd(playerKey ?? "", _ => new Dictionary<string, long>());
            lock (bag)
            {
                if (!bag.TryGetValue(itemId, out var have) || have < count) return Task.FromResult(false);
                var left = have - count;
                if (left > 0) bag[itemId] = left; else bag.Remove(itemId);
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>Settings for the inventory service.</summary>
    public sealed class InventoryOptions
    {
        /// <summary>
        /// Maps a connected peer to the stable player key its inventory is stored under. Defaults to the peer's
        /// connection id — override (e.g. to the authenticated account id from <c>SetNet.Auth</c>) so a player's
        /// inventory survives reconnects and follows them across nodes.
        /// </summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
    }

    // ---- wire ----

    internal static class InventoryCodec
    {
        public static byte[] EncodeSnapshot(int correlationId, IReadOnlyList<ItemStack> stacks)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(correlationId);
            w.Write(stacks.Count);
            foreach (var s in stacks) { w.Write(s.ItemId ?? ""); w.Write(s.Count); }
            return ms.ToArray();
        }

        public static (int Correlation, List<ItemStack> Stacks) DecodeSnapshot(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32();
            var count = r.ReadInt32();
            var stacks = new List<ItemStack>(count);
            for (var i = 0; i < count; i++) stacks.Add(new ItemStack(r.ReadString(), r.ReadInt64()));
            return (corr, stacks);
        }
    }

    // ---- client ----

    internal static class InventoryRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<List<ItemStack>>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<List<ItemStack>>>();
        private static readonly ConcurrentDictionary<InventoryClient, byte> Clients
            = new ConcurrentDictionary<InventoryClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<List<ItemStack>> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, List<ItemStack> stacks) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(stacks); }
        public static void RegisterClient(InventoryClient c) => Clients[c] = 0;
        public static void DispatchEvent(List<ItemStack> stacks) { foreach (var c in Clients.Keys) c.OnChanged(stacks); }
    }

    /// <summary>
    /// Client-side inventory driver, attached by <see cref="InventoryClientExtensions.UseInventory"/>. Read-only by
    /// design (the server is authoritative): fetch the current inventory and subscribe to server-pushed changes.
    /// Grants and revokes happen in server game logic via <see cref="InventoryServer"/>.
    /// </summary>
    public sealed class InventoryClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised whenever the server pushes an updated inventory snapshot for this player.</summary>
        public event Action<IReadOnlyList<ItemStack>>? Changed;

        internal InventoryClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            InventoryRegistry.RegisterClient(this);
        }

        /// <summary>Fetches this player's current inventory from the server.</summary>
        public async Task<IReadOnlyList<ItemStack>> GetAsync()
        {
            var id = InventoryRegistry.NextId();
            var tcs = new TaskCompletionSource<List<ItemStack>>(TaskCreationOptions.RunContinuationsAsynchronously);
            InventoryRegistry.Register(id, tcs);
            try
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) w.Write(id);
                await _client.SendAsync(InventoryTypes.Command, ms.ToArray(), DeliveryMethod.Reliable).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new InventoryException("Inventory query timed out."); }
                }
            }
            finally { InventoryRegistry.Remove(id); }
        }

        internal void OnChanged(List<ItemStack> stacks) => Changed?.Invoke(stacks);
    }

    // ---- server ----

    /// <summary>
    /// Server-side inventory hub, attached by <see cref="InventoryServerExtensions.UseInventory"/>. The authority for
    /// item ownership: game logic calls <see cref="GrantAsync"/>/<see cref="TryRevokeAsync"/> by player key (works
    /// whether or not the player is online), and connected players are pushed a fresh snapshot on every change.
    /// Other packages (<c>SetNet.Trade</c>, <c>SetNet.Mail</c>) move items through this same hub so ownership stays
    /// consistent.
    /// </summary>
    public sealed class InventoryServer
    {
        private static readonly ConcurrentDictionary<BaseServer, InventoryServer> Servers = new ConcurrentDictionary<BaseServer, InventoryServer>();

        private readonly InventoryOptions _options;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        /// <summary>The backing store (default in-process; swap for Redis/DB). Shared with trade/mail so item moves are consistent.</summary>
        public IInventoryStore Store { get; }

        internal InventoryServer(IInventoryStore store, InventoryOptions options)
        {
            Store = store;
            _options = options;
        }

        internal static InventoryServer Enable(BaseServer server, IInventoryStore? store, InventoryOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new InventoryServer(store ?? new MemoryInventoryStore(), options ?? new InventoryOptions());
                s.PeerConnected += peer => hub._online[hub._options.PlayerKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var current) && ReferenceEquals(current, peer))
                        hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        internal static InventoryServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Resolves the stable player key for a connected peer (per the configured resolver).</summary>
        public string KeyOf(BasePeer peer) => _options.PlayerKey(peer);

        /// <summary>The connected peer for a player key, or null when that player is offline.</summary>
        public BasePeer? PeerFor(string playerKey) => _online.TryGetValue(playerKey, out var peer) ? peer : null;

        /// <summary>Grants items to a player (online or not) and pushes them a fresh snapshot if connected.</summary>
        public async Task GrantAsync(string playerKey, string itemId, long count)
        {
            await Store.GrantAsync(playerKey, itemId, count).ConfigureAwait(false);
            await PushAsync(playerKey).ConfigureAwait(false);
        }

        /// <summary>Atomically revokes items from a player; returns false if they hold fewer. Pushes a snapshot on success.</summary>
        public async Task<bool> TryRevokeAsync(string playerKey, string itemId, long count)
        {
            var ok = await Store.TryRevokeAsync(playerKey, itemId, count).ConfigureAwait(false);
            if (ok) await PushAsync(playerKey).ConfigureAwait(false);
            return ok;
        }

        /// <summary>Returns a player's current inventory.</summary>
        public Task<IReadOnlyList<ItemStack>> GetAsync(string playerKey) => Store.GetAsync(playerKey);

        /// <summary>Pushes a fresh snapshot to a player if they're online (used by trade/mail after moving items).</summary>
        public async Task PushAsync(string playerKey)
        {
            if (!_online.TryGetValue(playerKey, out var peer)) return;
            var stacks = await Store.GetAsync(playerKey).ConfigureAwait(false);
            try { await peer.SendAsync(InventoryTypes.Event, InventoryCodec.EncodeSnapshot(0, stacks), DeliveryMethod.Reliable).ConfigureAwait(false); }
            catch { /* peer dropped mid-push */ }
        }

        internal async Task OnQuery(BasePeer peer, int correlationId)
        {
            var stacks = await Store.GetAsync(_options.PlayerKey(peer)).ConfigureAwait(false);
            try { await peer.SendAsync(InventoryTypes.Reply, InventoryCodec.EncodeSnapshot(correlationId, stacks), DeliveryMethod.Reliable).ConfigureAwait(false); }
            catch { /* peer dropped */ }
        }
    }

    // ---- auto-discovered handlers ----

    /// <summary>Auto-discovered server handler for inventory queries.</summary>
    [MessageHandler(InventoryTypes.Command)]
    public sealed class InventoryCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = InventoryServer.For(peer.CurrentPeerInfo.Server);
            if (hub == null) return Task.CompletedTask;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return hub.OnQuery(peer, r.ReadInt32());
        }
    }

    /// <summary>Auto-discovered client handler for correlated inventory snapshots.</summary>
    [MessageHandler(InventoryTypes.Reply)]
    public sealed class InventoryReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, stacks) = InventoryCodec.DecodeSnapshot(data); InventoryRegistry.Complete(corr, stacks); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for pushed inventory changes.</summary>
    [MessageHandler(InventoryTypes.Event)]
    public sealed class InventoryEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (_, stacks) = InventoryCodec.DecodeSnapshot(data); InventoryRegistry.DispatchEvent(stacks); return Task.CompletedTask; }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the inventory hub to a server by composition.</summary>
    public static class InventoryServerExtensions
    {
        /// <summary>Enables the server-side inventory hub; returns it so game logic can grant/revoke items.</summary>
        public static InventoryServer UseInventory(this BaseServer server, IInventoryStore? store = null, InventoryOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return InventoryServer.Enable(server, store, options);
        }
    }

    /// <summary>Attaches an inventory driver to a client by composition.</summary>
    public static class InventoryClientExtensions
    {
        /// <summary>Enables client-side inventory; returns the driver (<c>GetAsync</c> + <c>Changed</c> event).</summary>
        public static InventoryClient UseInventory(this BaseClient client) => new InventoryClient(client);
    }

    /// <summary>One-time bootstrap so the inventory handlers are discovered. Call at startup.</summary>
    public static class InventoryRuntime
    {
        /// <summary>Ensures the inventory layer is discoverable.</summary>
        public static void Enable() { _ = InventoryTypes.Command; }
    }
}
