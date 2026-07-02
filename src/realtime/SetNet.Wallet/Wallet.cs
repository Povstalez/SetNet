using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.Wallet
{
    /// <summary>Command operations (client → server) within the Wallet protocol channel.</summary>
    internal enum WalletOp : ushort { Query = 1 }

    /// <summary>Push events (server → client) within the Wallet protocol channel.</summary>
    internal enum WalletEvt : ushort { Changed = 10 }

    /// <summary>Thrown when a wallet operation fails (query timeout).</summary>
    public sealed class WalletException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public WalletException(string message) : base(message) { }
    }

    /// <summary>One currency balance: a named currency and its amount.</summary>
    public sealed class CurrencyBalance
    {
        /// <summary>The currency's id (e.g. "gold", "gems").</summary>
        public string Currency { get; set; } = "";

        /// <summary>The amount held (≥ 0).</summary>
        public long Amount { get; set; }

        /// <summary>Creates an empty balance (for serialization).</summary>
        public CurrencyBalance() { }

        /// <summary>Creates a balance of <paramref name="amount"/> of <paramref name="currency"/>.</summary>
        public CurrencyBalance(string currency, long amount) { Currency = currency; Amount = amount; }
    }

    // ---- store ----

    /// <summary>
    /// Persistence for player wallets. The default is <see cref="MemoryWalletStore"/> (in-process); supply a
    /// Redis/DB store for durability and cross-node sharing. <see cref="TryWithdrawAsync"/> and
    /// <see cref="TryTransferAsync"/> must be atomic — they are the anti-dupe / anti-overdraft guarantees.
    /// </summary>
    public interface IWalletStore
    {
        /// <summary>Returns a player's balances (empty when unknown).</summary>
        Task<IReadOnlyList<CurrencyBalance>> GetAsync(string playerKey);

        /// <summary>Adds <paramref name="amount"/> of <paramref name="currency"/> to the player's wallet.</summary>
        Task DepositAsync(string playerKey, string currency, long amount);

        /// <summary>Atomically removes <paramref name="amount"/>; returns false (changing nothing) on insufficient funds.</summary>
        Task<bool> TryWithdrawAsync(string playerKey, string currency, long amount);

        /// <summary>Atomically moves <paramref name="amount"/> of <paramref name="currency"/> between players; false on insufficient funds.</summary>
        Task<bool> TryTransferAsync(string fromKey, string toKey, string currency, long amount);
    }

    /// <summary>In-process wallet store. Fine for a single node / tests; swap for a shared store to persist or cluster.</summary>
    public sealed class MemoryWalletStore : IWalletStore
    {
        private readonly ConcurrentDictionary<string, Dictionary<string, long>> _wallets = new ConcurrentDictionary<string, Dictionary<string, long>>();
        // A single lock ordering guards cross-wallet transfers against deadlock.
        private readonly object _transferGate = new object();

        private Dictionary<string, long> Wallet(string key) => _wallets.GetOrAdd(key ?? "", _ => new Dictionary<string, long>());

        /// <inheritdoc/>
        public Task<IReadOnlyList<CurrencyBalance>> GetAsync(string playerKey)
        {
            var w = Wallet(playerKey);
            List<CurrencyBalance> list;
            lock (w) { list = new List<CurrencyBalance>(w.Count); foreach (var kv in w) if (kv.Value > 0) list.Add(new CurrencyBalance(kv.Key, kv.Value)); }
            return Task.FromResult<IReadOnlyList<CurrencyBalance>>(list);
        }

        /// <inheritdoc/>
        public Task DepositAsync(string playerKey, string currency, long amount)
        {
            if (amount <= 0) return Task.CompletedTask;
            var w = Wallet(playerKey);
            lock (w) { w.TryGetValue(currency, out var have); w[currency] = have + amount; }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<bool> TryWithdrawAsync(string playerKey, string currency, long amount)
        {
            if (amount <= 0) return Task.FromResult(true);
            var w = Wallet(playerKey);
            lock (w)
            {
                if (!w.TryGetValue(currency, out var have) || have < amount) return Task.FromResult(false);
                var left = have - amount;
                if (left > 0) w[currency] = left; else w.Remove(currency);
                return Task.FromResult(true);
            }
        }

        /// <inheritdoc/>
        public Task<bool> TryTransferAsync(string fromKey, string toKey, string currency, long amount)
        {
            if (amount <= 0) return Task.FromResult(true);
            // Serialize all transfers so a withdraw+deposit pair is atomic without per-wallet lock ordering hazards.
            lock (_transferGate)
            {
                var from = Wallet(fromKey);
                long have;
                lock (from) { if (!from.TryGetValue(currency, out have) || have < amount) return Task.FromResult(false); }
                lock (from) { var left = have - amount; if (left > 0) from[currency] = left; else from.Remove(currency); }
                var to = Wallet(toKey);
                lock (to) { to.TryGetValue(currency, out var toHave); to[currency] = toHave + amount; }
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>Settings for the wallet service.</summary>
    public sealed class WalletOptions
    {
        /// <summary>Maps a connected peer to its stable player key (default = connection id; override for durable wallets).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
    }

    // ---- wire ----

    internal static class WalletCodec
    {
        public static byte[] EncodeBalances(IReadOnlyList<CurrencyBalance> balances)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(balances.Count);
            foreach (var b in balances) { w.Write(b.Currency ?? ""); w.Write(b.Amount); }
            return ms.ToArray();
        }

        public static List<CurrencyBalance> DecodeBalances(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<CurrencyBalance>();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var count = r.ReadInt32();
            var list = new List<CurrencyBalance>(count);
            for (var i = 0; i < count; i++) list.Add(new CurrencyBalance(r.ReadString(), r.ReadInt64()));
            return list;
        }
    }

    // ---- client ----

    /// <summary>
    /// Client-side wallet driver, attached by <see cref="WalletClientExtensions.UseWallet"/>. Read-only (the server
    /// is authoritative): fetch balances and subscribe to changes. Deposits/withdrawals happen in server game logic.
    /// Rides the unified protocol on the <see cref="Channels.Wallet"/> channel.
    /// </summary>
    public sealed class WalletClient
    {
        private readonly BaseClient _client;
        private readonly IDisposable _subscription;

        /// <summary>Raised when the server pushes updated balances for this player.</summary>
        public event Action<IReadOnlyList<CurrencyBalance>>? Changed;

        internal WalletClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscription = _client.OnRaw(Channels.Wallet, (ushort)WalletEvt.Changed,
                body => Changed?.Invoke(WalletCodec.DecodeBalances(body)));
        }

        /// <summary>Fetches this player's current balances.</summary>
        public async Task<IReadOnlyList<CurrencyBalance>> GetAsync()
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.Wallet, (ushort)WalletOp.Query, Array.Empty<byte>()).ConfigureAwait(false);
                return WalletCodec.DecodeBalances(body);
            }
            catch (ProtocolException ex) { throw new WalletException(ex.Message); }
            catch (TimeoutException) { throw new WalletException("Wallet query timed out."); }
        }
    }

    // ---- server ----

    /// <summary>
    /// Server-side wallet hub, attached by <see cref="WalletServerExtensions.UseWallet"/>. The authority for player
    /// currency: game logic deposits/withdraws/transfers by player key, and connected players are pushed fresh
    /// balances on every change. Vendors and auctions move currency through this same hub.
    /// </summary>
    public sealed class WalletServer
    {
        private static readonly ConcurrentDictionary<BaseServer, WalletServer> Servers = new ConcurrentDictionary<BaseServer, WalletServer>();

        private readonly WalletOptions _options;
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        /// <summary>The backing store (default in-process; swap for Redis/DB).</summary>
        public IWalletStore Store { get; }

        internal WalletServer(IWalletStore store, WalletOptions options) { Store = store; _options = options; }

        internal static WalletServer Enable(BaseServer server, IWalletStore? store, WalletOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new WalletServer(store ?? new MemoryWalletStore(), options ?? new WalletOptions());
                s.PeerConnected += peer => hub._online[hub._options.PlayerKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.PlayerKey(peer);
                    if (hub._online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) hub._online.TryRemove(key, out _);
                };
                return hub;
            });

        internal static WalletServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>Resolves the stable player key for a connected peer.</summary>
        public string KeyOf(BasePeer peer) => _options.PlayerKey(peer);

        /// <summary>Adds currency to a player and pushes fresh balances if online.</summary>
        public async Task DepositAsync(string playerKey, string currency, long amount)
        {
            await Store.DepositAsync(playerKey, currency, amount).ConfigureAwait(false);
            await PushAsync(playerKey).ConfigureAwait(false);
        }

        /// <summary>Atomically removes currency; false on insufficient funds. Pushes on success.</summary>
        public async Task<bool> TryWithdrawAsync(string playerKey, string currency, long amount)
        {
            var ok = await Store.TryWithdrawAsync(playerKey, currency, amount).ConfigureAwait(false);
            if (ok) await PushAsync(playerKey).ConfigureAwait(false);
            return ok;
        }

        /// <summary>Atomically transfers currency between players; false on insufficient funds. Pushes both on success.</summary>
        public async Task<bool> TryTransferAsync(string fromKey, string toKey, string currency, long amount)
        {
            var ok = await Store.TryTransferAsync(fromKey, toKey, currency, amount).ConfigureAwait(false);
            if (ok) { await PushAsync(fromKey).ConfigureAwait(false); await PushAsync(toKey).ConfigureAwait(false); }
            return ok;
        }

        /// <summary>Returns a player's balances.</summary>
        public Task<IReadOnlyList<CurrencyBalance>> GetAsync(string playerKey) => Store.GetAsync(playerKey);

        /// <summary>Pushes fresh balances to a player if online.</summary>
        public async Task PushAsync(string playerKey)
        {
            if (!_online.TryGetValue(playerKey, out var peer)) return;
            var balances = await Store.GetAsync(playerKey).ConfigureAwait(false);
            try { await peer.PublishRawAsync(Channels.Wallet, (ushort)WalletEvt.Changed, WalletCodec.EncodeBalances(balances)).ConfigureAwait(false); } catch { }
        }

        internal async Task HandleQueryAsync(ChannelRequest request)
        {
            var balances = await Store.GetAsync(_options.PlayerKey(request.Peer)).ConfigureAwait(false);
            await request.ReplyRawAsync(WalletCodec.EncodeBalances(balances)).ConfigureAwait(false);
        }
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for wallet queries.</summary>
    [ProtocolChannel(Channels.Wallet)]
    public sealed class WalletChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = WalletServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("wallet is not configured on this server");
            return hub.HandleQueryAsync(request);
        }
    }

    // ---- composition entry points ----

    /// <summary>Attaches the wallet hub to a server by composition.</summary>
    public static class WalletServerExtensions
    {
        /// <summary>Enables the server-side wallet hub; returns it so game logic can move currency.</summary>
        public static WalletServer UseWallet(this BaseServer server, IWalletStore? store = null, WalletOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return WalletServer.Enable(server, store, options);
        }
    }

    /// <summary>Attaches a wallet driver to a client by composition.</summary>
    public static class WalletClientExtensions
    {
        /// <summary>Enables client-side wallet; returns the driver (<c>GetAsync</c> + <c>Changed</c>).</summary>
        public static WalletClient UseWallet(this BaseClient client) => new WalletClient(client);
    }

    /// <summary>One-time bootstrap so the wallet channel service is discovered. Call at startup.</summary>
    public static class WalletRuntime
    {
        /// <summary>Ensures the wallet layer is discoverable.</summary>
        public static void Enable() { _ = typeof(WalletChannelService); }
    }
}
