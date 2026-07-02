using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Core.Transport;
using SetNet.Data;
using SetNet.Data.Attributes;

namespace SetNet.Progression
{
    /// <summary>Reserved wire types for the progression service. Don't reuse these ids for application messages.</summary>
    public static class ProgressionTypes
    {
        /// <summary>Client → server: state query.</summary>
        public const ushort Command = ushort.MaxValue - 74;   // 65461

        /// <summary>Server → client: correlated reply (a progression snapshot).</summary>
        public const ushort Reply = ushort.MaxValue - 75;     // 65460

        /// <summary>Server → client: push event when the peer's level/XP changes.</summary>
        public const ushort Event = ushort.MaxValue - 76;     // 65459
    }

    /// <summary>Thrown when a progression operation fails (query timeout).</summary>
    public sealed class ProgressionException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public ProgressionException(string message) : base(message) { }
    }

    /// <summary>A player's progression snapshot: level, current XP into that level, and XP required for the next.</summary>
    public sealed class ProgressionState
    {
        /// <summary>The player's current level (starts at 1).</summary>
        public int Level { get; set; } = 1;

        /// <summary>XP accumulated toward the next level.</summary>
        public long Xp { get; set; }

        /// <summary>XP required to reach the next level from the start of the current one (0 at the cap).</summary>
        public long XpToNext { get; set; }

        /// <summary>Creates an empty state.</summary>
        public ProgressionState() { }

        /// <summary>Creates a state with the given level, XP, and XP-to-next.</summary>
        public ProgressionState(int level, long xp, long xpToNext) { Level = level; Xp = xp; XpToNext = xpToNext; }
    }

    // ---- store ----

    /// <summary>
    /// Persistence for player progression. The default is <see cref="MemoryProgressionStore"/> (in-process); supply a
    /// Redis/DB store for durability. Stores only the raw (level, xp) pair; the level curve is applied by the hub.
    /// </summary>
    public interface IProgressionStore
    {
        /// <summary>Returns a player's (level, xp); (1, 0) when unknown.</summary>
        Task<(int Level, long Xp)> GetAsync(string playerKey);

        /// <summary>Persists a player's (level, xp).</summary>
        Task SetAsync(string playerKey, int level, long xp);
    }

    /// <summary>In-process progression store.</summary>
    public sealed class MemoryProgressionStore : IProgressionStore
    {
        private readonly ConcurrentDictionary<string, (int Level, long Xp)> _players = new ConcurrentDictionary<string, (int, long)>();

        /// <inheritdoc/>
        public Task<(int Level, long Xp)> GetAsync(string playerKey)
            => Task.FromResult(_players.TryGetValue(playerKey ?? "", out var v) ? v : (1, 0L));

        /// <inheritdoc/>
        public Task SetAsync(string playerKey, int level, long xp) { _players[playerKey ?? ""] = (level, xp); return Task.CompletedTask; }
    }

    /// <summary>Settings for the progression service.</summary>
    public sealed class ProgressionOptions
    {
        /// <summary>Maps a connected peer to its stable player key (default = connection id; override for durable progression).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>
        /// XP required to advance <b>from</b> the given level to the next. Default is a gentle quadratic
        /// (<c>100 · level</c>). Return 0 or less to mark a level cap (further XP is ignored).
        /// </summary>
        public Func<int, long> XpForLevel { get; set; } = level => 100L * level;

        /// <summary>Hard level cap; XP beyond it is discarded (default 100).</summary>
        public int MaxLevel { get; set; } = 100;
    }

    // ---- wire ----

    internal static class ProgressionCodec
    {
        public static byte[] EncodeState(int corr, ProgressionState s)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(s.Level); w.Write(s.Xp); w.Write(s.XpToNext);
            return ms.ToArray();
        }

        public static (int Corr, ProgressionState State) DecodeState(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32();
            return (corr, new ProgressionState(r.ReadInt32(), r.ReadInt64(), r.ReadInt64()));
        }
    }

    internal static class ProgressionRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<ProgressionState>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<ProgressionState>>();
        private static readonly ConcurrentDictionary<ProgressionClient, byte> Clients = new ConcurrentDictionary<ProgressionClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<ProgressionState> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, ProgressionState s) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(s); }
        public static void RegisterClient(ProgressionClient c) => Clients[c] = 0;
        public static void DispatchEvent(ProgressionState s) { foreach (var c in Clients.Keys) c.OnChanged(s); }
    }

    /// <summary>
    /// Client-side progression driver, attached by <see cref="ProgressionClientExtensions.UseProgression"/>. Read-only
    /// (the server awards XP): fetch level/XP and subscribe to changes to drive an XP bar and level-up effects.
    /// </summary>
    public sealed class ProgressionClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when the server pushes updated level/XP for this player.</summary>
        public event Action<ProgressionState>? Changed;

        internal ProgressionClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ProgressionRegistry.RegisterClient(this);
        }

        /// <summary>Fetches this player's current level and XP.</summary>
        public async Task<ProgressionState> GetAsync()
        {
            var id = ProgressionRegistry.NextId();
            var tcs = new TaskCompletionSource<ProgressionState>(TaskCreationOptions.RunContinuationsAsynchronously);
            ProgressionRegistry.Register(id, tcs);
            try
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms)) w.Write(id);
                await _client.SendAsync(ProgressionTypes.Command, ms.ToArray(), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new ProgressionException("Progression query timed out."); }
                }
            }
            finally { ProgressionRegistry.Remove(id); }
        }

        internal void OnChanged(ProgressionState s) => Changed?.Invoke(s);
    }

    /// <summary>
    /// Server-side progression hub, attached by <see cref="ProgressionServerExtensions.UseProgression"/>. Game logic
    /// awards XP by player key; the hub applies the level curve (rolling over multiple levels at once), persists,
    /// fires <see cref="LeveledUp"/> for each level gained (so the app can grant rewards), and pushes the new state
    /// to the online player.
    /// </summary>
    public sealed class ProgressionServer
    {
        private static readonly ConcurrentDictionary<BaseServer, ProgressionServer> Servers = new ConcurrentDictionary<BaseServer, ProgressionServer>();

        private readonly IProgressionStore _store;
        private readonly ProgressionOptions _options;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        /// <summary>Raised once per level gained (args: player key, the level just reached).</summary>
        public event Action<string, int>? LeveledUp;

        internal ProgressionServer(IProgressionStore store, ProgressionOptions options) { _store = store; _options = options; }

        internal static ProgressionServer Enable(BaseServer server, IProgressionStore? store, ProgressionOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new ProgressionServer(store ?? new MemoryProgressionStore(), options ?? new ProgressionOptions());
                s.PeerConnected += peer => hub._online[hub._options.PlayerKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        internal static ProgressionServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Resolves the stable player key for a connected peer.</summary>
        public string KeyOf(BasePeer peer) => _options.PlayerKey(peer);

        /// <summary>Returns a player's current state (level, XP, XP-to-next).</summary>
        public async Task<ProgressionState> GetAsync(string playerKey)
        {
            var (level, xp) = await _store.GetAsync(playerKey).ConfigureAwait(false);
            return Snapshot(level, xp);
        }

        /// <summary>
        /// Awards <paramref name="amount"/> XP to a player, rolling over as many levels as it fills. Fires
        /// <see cref="LeveledUp"/> per level and pushes the new state if online; returns the resulting state.
        /// </summary>
        public async Task<ProgressionState> GrantXpAsync(string playerKey, long amount)
        {
            if (amount <= 0) return await GetAsync(playerKey).ConfigureAwait(false);

            var (level, xp) = await _store.GetAsync(playerKey).ConfigureAwait(false);
            xp += amount;

            var gained = new System.Collections.Generic.List<int>();
            while (level < _options.MaxLevel)
            {
                var need = _options.XpForLevel(level);
                if (need <= 0 || xp < need) break;
                xp -= need;
                level++;
                gained.Add(level);
            }
            if (level >= _options.MaxLevel) xp = 0;   // clamp at the cap

            await _store.SetAsync(playerKey, level, xp).ConfigureAwait(false);
            foreach (var lvl in gained) LeveledUp?.Invoke(playerKey, lvl);

            var state = Snapshot(level, xp);
            await PushAsync(playerKey, state).ConfigureAwait(false);
            return state;
        }

        /// <summary>Pushes a state snapshot to a player if online.</summary>
        public async Task PushAsync(string playerKey, ProgressionState? state = null)
        {
            if (!_online.TryGetValue(playerKey, out var peer)) return;
            state ??= await GetAsync(playerKey).ConfigureAwait(false);
            try { await peer.SendAsync(ProgressionTypes.Event, ProgressionCodec.EncodeState(0, state), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { }
        }

        private ProgressionState Snapshot(int level, long xp)
        {
            var need = level < _options.MaxLevel ? Math.Max(0, _options.XpForLevel(level)) : 0;
            return new ProgressionState(level, xp, need);
        }

        internal async Task OnQuery(BasePeer peer, int correlationId)
        {
            var state = await GetAsync(_options.PlayerKey(peer)).ConfigureAwait(false);
            try { await peer.SendAsync(ProgressionTypes.Reply, ProgressionCodec.EncodeState(correlationId, state), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Auto-discovered server handler for progression queries.</summary>
    [MessageHandler(ProgressionTypes.Command)]
    public sealed class ProgressionCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = ProgressionServer.For(peer.CurrentPeerInfo.Server);
            if (hub == null) return Task.CompletedTask;
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return hub.OnQuery(peer, r.ReadInt32());
        }
    }

    /// <summary>Auto-discovered client handler for correlated progression snapshots.</summary>
    [MessageHandler(ProgressionTypes.Reply)]
    public sealed class ProgressionReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, s) = ProgressionCodec.DecodeState(data); ProgressionRegistry.Complete(corr, s); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for pushed progression changes.</summary>
    [MessageHandler(ProgressionTypes.Event)]
    public sealed class ProgressionEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (_, s) = ProgressionCodec.DecodeState(data); ProgressionRegistry.DispatchEvent(s); return Task.CompletedTask; }
    }

    /// <summary>Attaches the progression hub to a server by composition.</summary>
    public static class ProgressionServerExtensions
    {
        /// <summary>Enables the server-side progression hub; returns it so game logic can award XP.</summary>
        public static ProgressionServer UseProgression(this BaseServer server, IProgressionStore? store = null, ProgressionOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return ProgressionServer.Enable(server, store, options);
        }
    }

    /// <summary>Attaches a progression driver to a client by composition.</summary>
    public static class ProgressionClientExtensions
    {
        /// <summary>Enables client-side progression; returns the driver (<c>GetAsync</c> + <c>Changed</c>).</summary>
        public static ProgressionClient UseProgression(this BaseClient client) => new ProgressionClient(client);
    }

    /// <summary>One-time bootstrap so the progression handlers are discovered. Call at startup.</summary>
    public static class ProgressionRuntime
    {
        /// <summary>Ensures the progression layer is discoverable.</summary>
        public static void Enable() { _ = ProgressionTypes.Command; }
    }
}
