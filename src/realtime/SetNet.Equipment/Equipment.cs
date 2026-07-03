using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Inventory;
using SetNet.Protocol;
using SetNet.Stats;

namespace SetNet.Equipment
{
    /// <summary>Command operations (client → server) within the Equipment channel.</summary>
    internal enum EquipOp : ushort { Equip = 1, Unequip = 2, Query = 3 }

    /// <summary>Push events (server → client) within the Equipment channel.</summary>
    internal enum EquipEvt : ushort { Changed = 10 }

    /// <summary>Thrown when an equipment operation fails.</summary>
    public sealed class EquipmentException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public EquipmentException(string message) : base(message) { }
    }

    /// <summary>One equipment slot: its id and an optional rule for which item ids may go in it.</summary>
    public sealed class SlotDefinition
    {
        /// <summary>The slot id (your own: "head", "weapon", "ring1"…).</summary>
        public string SlotId { get; }
        /// <summary>Optional predicate — only item ids it accepts may be equipped here (null = anything).</summary>
        public Func<string, bool>? Accepts { get; }
        /// <summary>Creates a slot.</summary>
        public SlotDefinition(string slotId, Func<string, bool>? accepts = null) { SlotId = slotId; Accepts = accepts; }
    }

    /// <summary>Your character's slot layout — fully custom. Build with <see cref="Create"/>.</summary>
    public sealed class EquipmentSchema
    {
        private readonly Dictionary<string, SlotDefinition> _slots;
        private EquipmentSchema(Dictionary<string, SlotDefinition> slots) => _slots = slots;

        /// <summary>The slot definition, or null if unknown.</summary>
        public SlotDefinition? Get(string slotId) => slotId != null && _slots.TryGetValue(slotId, out var s) ? s : null;
        /// <summary>All slot ids.</summary>
        public IEnumerable<string> SlotIds => _slots.Keys;

        /// <summary>Starts a fluent builder.</summary>
        public static Builder Create() => new Builder();

        /// <summary>Fluent builder for an <see cref="EquipmentSchema"/>.</summary>
        public sealed class Builder
        {
            private readonly Dictionary<string, SlotDefinition> _slots = new Dictionary<string, SlotDefinition>();
            /// <summary>Declares a slot with an optional accept rule.</summary>
            public Builder Slot(string slotId, Func<string, bool>? accepts = null) { _slots[slotId] = new SlotDefinition(slotId, accepts); return this; }
            /// <summary>Builds the schema.</summary>
            public EquipmentSchema Build() => new EquipmentSchema(new Dictionary<string, SlotDefinition>(_slots));
        }
    }

    /// <summary>Stores each player's equipped items (slot → item id). Default is in-process.</summary>
    public interface IEquipmentStore
    {
        /// <summary>The item in a slot, or null.</summary>
        string? Get(string playerKey, string slotId);
        /// <summary>Puts an item in a slot.</summary>
        void Set(string playerKey, string slotId, string itemId);
        /// <summary>Clears a slot; returns the removed item id, or null.</summary>
        string? Clear(string playerKey, string slotId);
        /// <summary>The full loadout (slot → item id).</summary>
        IReadOnlyDictionary<string, string> All(string playerKey);
    }

    /// <summary>In-process equipment store.</summary>
    public sealed class MemoryEquipmentStore : IEquipmentStore
    {
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _loadouts = new ConcurrentDictionary<string, Dictionary<string, string>>();
        private Dictionary<string, string> Loadout(string key) => _loadouts.GetOrAdd(key ?? "", _ => new Dictionary<string, string>());

        /// <inheritdoc/>
        public string? Get(string playerKey, string slotId) { var l = Loadout(playerKey); lock (l) return l.TryGetValue(slotId, out var v) ? v : null; }
        /// <inheritdoc/>
        public void Set(string playerKey, string slotId, string itemId) { var l = Loadout(playerKey); lock (l) l[slotId] = itemId; }
        /// <inheritdoc/>
        public string? Clear(string playerKey, string slotId) { var l = Loadout(playerKey); lock (l) { if (l.TryGetValue(slotId, out var v)) { l.Remove(slotId); return v; } return null; } }
        /// <inheritdoc/>
        public IReadOnlyDictionary<string, string> All(string playerKey) { var l = Loadout(playerKey); lock (l) return new Dictionary<string, string>(l); }
    }

    /// <summary>Settings + seams for the equipment hub.</summary>
    public sealed class EquipmentOptions
    {
        /// <summary>The slot layout (required).</summary>
        public EquipmentSchema Schema { get; set; } = EquipmentSchema.Create().Build();
        /// <summary>Maps an item id to the stat modifiers it grants while equipped (null = no stat effect).</summary>
        public Func<string, IEnumerable<StatModifier>?>? ItemStats { get; set; }
        /// <summary>Resolves a player key to their <see cref="StatSet"/> so equipped items can modify it (null = slots only).</summary>
        public Func<string, StatSet?>? StatsOf { get; set; }
        /// <summary>Maps a connected peer to its stable player key (must match the Inventory hub's key mapping).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
    }

    internal static class EquipCodec
    {
        public static byte[] EncodeLoadout(IReadOnlyDictionary<string, string> loadout)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(loadout.Count);
            foreach (var kv in loadout) { w.Write(kv.Key); w.Write(kv.Value); }
            return ms.ToArray();
        }

        public static Dictionary<string, string> DecodeLoadout(byte[] body)
        {
            var map = new Dictionary<string, string>();
            if (body == null || body.Length == 0) return map;
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++) map[r.ReadString()] = r.ReadString();
            return map;
        }

        public static byte[] EncodeEquip(string slotId, string itemId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(slotId ?? ""); w.Write(itemId ?? "");
            return ms.ToArray();
        }

        public static (string slotId, string itemId) DecodeEquip(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return (r.ReadString(), r.ReadString());
        }

        public static byte[] EncodeResult(bool ok, string message)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(ok); w.Write(message ?? "");
            return ms.ToArray();
        }
    }

    // ---- client ----

    /// <summary>Client-side equipment driver (from <see cref="EquipmentClientExtensions.UseEquipment"/>).</summary>
    public sealed class EquipmentClient
    {
        private readonly BaseClient _client;
        private readonly IDisposable _subscription;

        /// <summary>Raised when the server pushes an updated loadout (slot → item id).</summary>
        public event Action<IReadOnlyDictionary<string, string>>? Changed;

        internal EquipmentClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscription = _client.OnRaw(Channels.Equipment, (ushort)EquipEvt.Changed, body => Changed?.Invoke(EquipCodec.DecodeLoadout(body)));
        }

        /// <summary>Equips an item from inventory into a slot.</summary>
        public async Task EquipAsync(string slotId, string itemId)
        {
            var body = await Request(EquipOp.Equip, EquipCodec.EncodeEquip(slotId, itemId)).ConfigureAwait(false);
            ThrowIfFailed(body);
        }

        /// <summary>Unequips whatever is in a slot back to inventory.</summary>
        public async Task UnequipAsync(string slotId)
        {
            var body = await Request(EquipOp.Unequip, EquipCodec.EncodeEquip(slotId, "")).ConfigureAwait(false);
            ThrowIfFailed(body);
        }

        /// <summary>Fetches the current loadout (slot → item id).</summary>
        public async Task<IReadOnlyDictionary<string, string>> GetAsync()
        {
            var body = await Request(EquipOp.Query, Array.Empty<byte>()).ConfigureAwait(false);
            return EquipCodec.DecodeLoadout(body);
        }

        private async Task<byte[]> Request(EquipOp op, byte[] body)
        {
            try { return await _client.RequestRawAsync(Channels.Equipment, (ushort)op, body).ConfigureAwait(false); }
            catch (ProtocolException ex) { throw new EquipmentException(ex.Message); }
            catch (TimeoutException) { throw new EquipmentException("equipment request timed out"); }
        }

        private static void ThrowIfFailed(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var ok = r.ReadBoolean();
            var msg = r.ReadString();
            if (!ok) throw new EquipmentException(msg);
        }
    }

    // ---- server ----

    /// <summary>
    /// Server-side equipment hub (from <see cref="EquipmentServerExtensions.UseEquipment"/>). Equips/unequips items to
    /// custom slots, moving them in and out of <see cref="InventoryServer"/>, and applies each equipped item's stat
    /// modifiers to the wearer's <see cref="StatSet"/> (removed on unequip).
    /// </summary>
    public sealed class EquipmentServer
    {
        private static readonly ConcurrentDictionary<BaseServer, EquipmentServer> Servers = new ConcurrentDictionary<BaseServer, EquipmentServer>();

        private readonly InventoryServer _inventory;
        private readonly EquipmentOptions _options;
        private readonly IEquipmentStore _store;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        internal EquipmentServer(InventoryServer inventory, IEquipmentStore store, EquipmentOptions options)
        { _inventory = inventory; _store = store; _options = options; }

        internal static EquipmentServer Enable(BaseServer server, InventoryServer inventory, IEquipmentStore? store, EquipmentOptions options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new EquipmentServer(inventory, store ?? new MemoryEquipmentStore(), options);
                s.PeerConnected += peer => hub._online[hub._options.PlayerKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        internal static EquipmentServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        private static string SlotSource(string slotId) => "equip:" + slotId;

        /// <summary>Equips an item (taken from the player's inventory) into a slot, swapping out any current item. Returns false with a reason on failure.</summary>
        public async Task<(bool ok, string message)> EquipAsync(string playerKey, string slotId, string itemId)
        {
            var slot = _options.Schema.Get(slotId);
            if (slot == null) return (false, $"unknown slot '{slotId}'");
            if (slot.Accepts != null && !slot.Accepts(itemId)) return (false, "that item doesn't fit this slot");

            if (!await _inventory.TryRevokeAsync(playerKey, itemId, 1).ConfigureAwait(false)) return (false, "you don't have that item");

            // Swap out whatever's already there.
            var prev = _store.Get(playerKey, slotId);
            if (prev != null)
            {
                await _inventory.GrantAsync(playerKey, prev, 1).ConfigureAwait(false);
                _options.StatsOf?.Invoke(playerKey)?.RemoveBySource(SlotSource(slotId));
            }

            _store.Set(playerKey, slotId, itemId);
            ApplyModifiers(playerKey, slotId, itemId);
            await PushAsync(playerKey).ConfigureAwait(false);
            return (true, "");
        }

        /// <summary>Unequips a slot's item back to the player's inventory. No-op (ok) if the slot is empty.</summary>
        public async Task<(bool ok, string message)> UnequipAsync(string playerKey, string slotId)
        {
            var cur = _store.Clear(playerKey, slotId);
            if (cur == null) return (true, "");
            await _inventory.GrantAsync(playerKey, cur, 1).ConfigureAwait(false);
            _options.StatsOf?.Invoke(playerKey)?.RemoveBySource(SlotSource(slotId));
            await PushAsync(playerKey).ConfigureAwait(false);
            return (true, "");
        }

        /// <summary>The player's current loadout (slot → item id).</summary>
        public IReadOnlyDictionary<string, string> GetEquipped(string playerKey) => _store.All(playerKey);

        private void ApplyModifiers(string playerKey, string slotId, string itemId)
        {
            var mods = _options.ItemStats?.Invoke(itemId);
            if (mods == null) return;
            var stats = _options.StatsOf?.Invoke(playerKey);
            if (stats == null) return;
            var source = SlotSource(slotId);
            var tagged = new List<StatModifier>();
            foreach (var m in mods) tagged.Add(new StatModifier(m.StatId, m.Op, m.Value, source));   // re-tag so unequip removes exactly this slot's
            stats.AddModifiers(tagged);
        }

        private async Task PushAsync(string playerKey)
        {
            if (!_online.TryGetValue(playerKey, out var peer)) return;
            try { await peer.PublishRawAsync(Channels.Equipment, (ushort)EquipEvt.Changed, EquipCodec.EncodeLoadout(_store.All(playerKey))).ConfigureAwait(false); } catch { }
        }

        internal async Task HandleAsync(ChannelRequest request)
        {
            var playerKey = _options.PlayerKey(request.Peer);
            switch (request.Op)
            {
                case (ushort)EquipOp.Equip:
                {
                    var (slotId, itemId) = EquipCodec.DecodeEquip(request.RawBody);
                    var (ok, msg) = await EquipAsync(playerKey, slotId, itemId).ConfigureAwait(false);
                    await request.ReplyRawAsync(EquipCodec.EncodeResult(ok, msg)).ConfigureAwait(false);
                    break;
                }
                case (ushort)EquipOp.Unequip:
                {
                    var (slotId, _) = EquipCodec.DecodeEquip(request.RawBody);
                    var (ok, msg) = await UnequipAsync(playerKey, slotId).ConfigureAwait(false);
                    await request.ReplyRawAsync(EquipCodec.EncodeResult(ok, msg)).ConfigureAwait(false);
                    break;
                }
                case (ushort)EquipOp.Query:
                    await request.ReplyRawAsync(EquipCodec.EncodeLoadout(_store.All(playerKey))).ConfigureAwait(false);
                    break;
                default:
                    throw new ProtocolException($"unknown equipment op {request.Op}");
            }
        }
    }

    /// <summary>Auto-discovered channel service for equipment.</summary>
    [ProtocolChannel(Channels.Equipment)]
    public sealed class EquipmentChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = EquipmentServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("equipment is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    /// <summary>Attaches the equipment hub to a server.</summary>
    public static class EquipmentServerExtensions
    {
        /// <summary>Enables the server-side equipment hub over an <see cref="InventoryServer"/>; returns it.</summary>
        public static EquipmentServer UseEquipment(this BaseServer server, InventoryServer inventory, EquipmentOptions options, IEquipmentStore? store = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return EquipmentServer.Enable(server, inventory, store, options);
        }
    }

    /// <summary>Attaches an equipment driver to a client.</summary>
    public static class EquipmentClientExtensions
    {
        /// <summary>Enables client-side equipment; returns the driver.</summary>
        public static EquipmentClient UseEquipment(this BaseClient client) => new EquipmentClient(client);
    }

    /// <summary>One-time bootstrap so the equipment channel service is discovered. Call at startup.</summary>
    public static class EquipmentRuntime
    {
        /// <summary>Ensures the equipment layer is discoverable.</summary>
        public static void Enable() { _ = typeof(EquipmentChannelService); }
    }
}
