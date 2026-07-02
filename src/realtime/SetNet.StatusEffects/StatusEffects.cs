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

namespace SetNet.StatusEffects
{
    /// <summary>Reserved wire types for the status-effect service. Don't reuse these ids for application messages.</summary>
    public static class StatusEffectTypes
    {
        /// <summary>Client → server: watch/unwatch/get command.</summary>
        public const ushort Command = ushort.MaxValue - 85;   // 65450

        /// <summary>Server → client: correlated reply (a target's effect list).</summary>
        public const ushort Reply = ushort.MaxValue - 86;     // 65449

        /// <summary>Server → client: push event when a watched target's effects change.</summary>
        public const ushort Event = ushort.MaxValue - 87;     // 65448
    }

    internal enum StatusOp : byte { Watch = 0, Unwatch = 1, Get = 2 }

    /// <summary>How re-applying an effect that's already present is resolved.</summary>
    public enum StackPolicy : byte
    {
        /// <summary>Reset the duration; stacks stay at the higher of the two (default).</summary>
        Refresh = 0,

        /// <summary>Add the incoming stacks (up to <see cref="StatusEffectDefinition.MaxStacks"/>) and refresh the duration.</summary>
        Stack = 1,

        /// <summary>Ignore the new application while one is already active.</summary>
        Ignore = 2,
    }

    /// <summary>Thrown when a status-effect operation fails (query timeout).</summary>
    public sealed class StatusEffectException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        public StatusEffectException(string message) : base(message) { }
    }

    /// <summary>Static rules for an effect type. Optional — unregistered effects use single-stack, refresh defaults.</summary>
    public sealed class StatusEffectDefinition
    {
        /// <summary>The effect type id (e.g. "poison", "haste").</summary>
        public string EffectId { get; set; } = "";

        /// <summary>Max simultaneous stacks (default 1).</summary>
        public int MaxStacks { get; set; } = 1;

        /// <summary>Default duration in milliseconds when an apply doesn't specify one (0 = permanent until removed).</summary>
        public long DefaultDurationMs { get; set; }

        /// <summary>How a re-application while active is resolved.</summary>
        public StackPolicy Stacking { get; set; } = StackPolicy.Refresh;

        /// <summary>True for a harmful effect (a hint for UI/cleanse logic; not enforced here).</summary>
        public bool IsDebuff { get; set; }

        /// <summary>Creates an empty definition.</summary>
        public StatusEffectDefinition() { }

        /// <summary>Creates a definition.</summary>
        public StatusEffectDefinition(string effectId, int maxStacks = 1, long defaultDurationMs = 0, StackPolicy stacking = StackPolicy.Refresh, bool isDebuff = false)
        { EffectId = effectId; MaxStacks = maxStacks; DefaultDurationMs = defaultDurationMs; Stacking = stacking; IsDebuff = isDebuff; }
    }

    /// <summary>A live instance of an effect on a target.</summary>
    public sealed class StatusEffect
    {
        /// <summary>The effect type id.</summary>
        public string EffectId { get; set; } = "";

        /// <summary>Current stack count.</summary>
        public int Stacks { get; set; } = 1;

        /// <summary>Application-defined strength (armor bonus, damage-per-tick, …).</summary>
        public double Magnitude { get; set; }

        /// <summary>Milliseconds until it expires (long.MaxValue = permanent).</summary>
        public long RemainingMs { get; set; }

        /// <summary>Who/what applied it (a player key, an ability id — opaque).</summary>
        public string Source { get; set; } = "";
    }

    /// <summary>Settings for the status-effect service.</summary>
    public sealed class StatusEffectOptions
    {
        /// <summary>
        /// Maps a connected peer to the target key its own effects are stored under (default = connection id). A
        /// target key is any string — a player key, or an entity id for mobs/NPCs (which have no peer and are read
        /// via <see cref="StatusEffectServer.GetAsync"/> or watched by nearby players).
        /// </summary>
        public Func<BasePeer, string> TargetKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();

        /// <summary>Sweep interval for expiring effects, in milliseconds (default 250).</summary>
        public int TickIntervalMs { get; set; } = 250;
    }

    // ---- wire ----

    internal static class StatusCodec
    {
        public static byte[] EncodeList(int corr, string targetKey, IReadOnlyList<StatusEffect> effects)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write(targetKey ?? "");
            w.Write(effects.Count);
            foreach (var e in effects) { w.Write(e.EffectId ?? ""); w.Write(e.Stacks); w.Write(e.Magnitude); w.Write(e.RemainingMs); w.Write(e.Source ?? ""); }
            return ms.ToArray();
        }

        public static (int Corr, string TargetKey, List<StatusEffect> Effects) DecodeList(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var corr = r.ReadInt32(); var target = r.ReadString();
            var count = r.ReadInt32();
            var list = new List<StatusEffect>(count);
            for (var i = 0; i < count; i++)
                list.Add(new StatusEffect { EffectId = r.ReadString(), Stacks = r.ReadInt32(), Magnitude = r.ReadDouble(), RemainingMs = r.ReadInt64(), Source = r.ReadString() });
            return (corr, target, list);
        }

        public static byte[] EncodeCommand(int corr, StatusOp op, string targetKey)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(corr); w.Write((byte)op); w.Write(targetKey ?? "");
            return ms.ToArray();
        }

        public static (int Corr, StatusOp Op, string TargetKey) DecodeCommand(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return (r.ReadInt32(), (StatusOp)r.ReadByte(), r.ReadString());
        }
    }

    internal static class StatusRegistry
    {
        private static int _counter;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<List<StatusEffect>>> Pending
            = new ConcurrentDictionary<int, TaskCompletionSource<List<StatusEffect>>>();
        private static readonly ConcurrentDictionary<StatusEffectClient, byte> Clients = new ConcurrentDictionary<StatusEffectClient, byte>();

        public static int NextId() => Interlocked.Increment(ref _counter);
        public static void Register(int id, TaskCompletionSource<List<StatusEffect>> tcs) => Pending[id] = tcs;
        public static void Remove(int id) => Pending.TryRemove(id, out _);
        public static void Complete(int id, List<StatusEffect> effects) { if (Pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(effects); }
        public static void RegisterClient(StatusEffectClient c) => Clients[c] = 0;
        public static void DispatchEvent(string targetKey, List<StatusEffect> effects) { foreach (var c in Clients.Keys) c.OnChanged(targetKey, effects); }
    }

    /// <summary>
    /// Client-side status-effect driver, attached by <see cref="StatusEffectClientExtensions.UseStatusEffects"/>.
    /// Read the effects on any target key and <see cref="WatchAsync"/> a target (your own buffs, or an enemy's
    /// debuffs) to receive live updates via <see cref="Changed"/>. Effects are applied by server game logic; the
    /// client only observes.
    /// </summary>
    public sealed class StatusEffectClient
    {
        private readonly BaseClient _client;

        /// <summary>Raised when a watched target's effect list changes (args: target key, current effects).</summary>
        public event Action<string, IReadOnlyList<StatusEffect>>? Changed;

        internal StatusEffectClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            StatusRegistry.RegisterClient(this);
        }

        /// <summary>Fetches a target's current effects.</summary>
        public Task<IReadOnlyList<StatusEffect>> GetAsync(string targetKey) => Send(StatusOp.Get, targetKey);

        /// <summary>Starts watching a target; you'll get its current effects now and a <see cref="Changed"/> on every update.</summary>
        public Task<IReadOnlyList<StatusEffect>> WatchAsync(string targetKey) => Send(StatusOp.Watch, targetKey);

        /// <summary>Stops watching a target.</summary>
        public Task UnwatchAsync(string targetKey) => Send(StatusOp.Unwatch, targetKey);

        private async Task<IReadOnlyList<StatusEffect>> Send(StatusOp op, string targetKey)
        {
            var id = StatusRegistry.NextId();
            var tcs = new TaskCompletionSource<List<StatusEffect>>(TaskCreationOptions.RunContinuationsAsynchronously);
            StatusRegistry.Register(id, tcs);
            try
            {
                await _client.SendAsync(StatusEffectTypes.Command, StatusCodec.EncodeCommand(id, op, targetKey), DeliveryMethod.Reliable).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new StatusEffectException("Status-effect command timed out."); }
                }
            }
            finally { StatusRegistry.Remove(id); }
        }

        internal void OnChanged(string targetKey, List<StatusEffect> effects) => Changed?.Invoke(targetKey, effects);
    }

    // ---- server ----

    internal sealed class LiveEffect
    {
        public string EffectId = "";
        public int Stacks;
        public double Magnitude;
        public long ExpiresAtMs;   // long.MaxValue = permanent
        public string Source = "";
    }

    /// <summary>
    /// Server-side status-effect hub, attached by <see cref="StatusEffectServerExtensions.UseStatusEffects"/>.
    /// The authority for buffs/debuffs on any <b>target key</b> (a player key or an entity id). Game logic applies
    /// and removes effects; a timer expires them; and every change is pushed to that target's watchers (and to the
    /// affected player, if the target is an online peer). Stacking/refresh follows an optional
    /// <see cref="StatusEffectDefinition"/>. This layer tracks the effects — interpreting a magnitude (armor,
    /// damage-over-time) is game logic.
    /// </summary>
    public sealed class StatusEffectServer : IDisposable
    {
        private static readonly ConcurrentDictionary<BaseServer, StatusEffectServer> Servers = new ConcurrentDictionary<BaseServer, StatusEffectServer>();

        private readonly StatusEffectOptions _options;
        private readonly ConcurrentDictionary<string, StatusEffectDefinition> _defs = new ConcurrentDictionary<string, StatusEffectDefinition>();
        private readonly ConcurrentDictionary<string, Dictionary<string, LiveEffect>> _targets = new ConcurrentDictionary<string, Dictionary<string, LiveEffect>>();
        private readonly ConcurrentDictionary<string, HashSet<BasePeer>> _watchers = new ConcurrentDictionary<string, HashSet<BasePeer>>();
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();
        private readonly Timer _timer;

        internal StatusEffectServer(StatusEffectOptions options)
        {
            _options = options;
            _timer = new Timer(_ => _ = Tick(), null, options.TickIntervalMs, options.TickIntervalMs);
        }

        internal static StatusEffectServer Enable(BaseServer server, StatusEffectOptions? options)
            => Servers.GetOrAdd(server, s =>
            {
                var hub = new StatusEffectServer(options ?? new StatusEffectOptions());
                s.PeerConnected += peer => hub._online[hub._options.TargetKey(peer)] = peer;
                s.PeerDisconnected += peer =>
                {
                    var key = hub._options.TargetKey(peer);
                    if (hub._online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) hub._online.TryRemove(key, out _);
                    foreach (var set in hub._watchers.Values) lock (set) set.Remove(peer);   // stop watching on disconnect
                };
                return hub;
            });

        internal static StatusEffectServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Registers (or replaces) an effect definition.</summary>
        public StatusEffectServer Define(StatusEffectDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.EffectId)) throw new ArgumentException("Effect definition needs a non-empty id.", nameof(def));
            _defs[def.EffectId] = def;
            return this;
        }

        /// <summary>
        /// Applies an effect to a target, honouring its definition's stack/refresh policy. A <paramref name="durationMs"/>
        /// of 0 uses the definition default (or permanent); pass a positive value to override. Pushes the change.
        /// </summary>
        public async Task ApplyAsync(string targetKey, string effectId, long durationMs = 0, int stacks = 1, double magnitude = 0, string source = "")
        {
            var def = _defs.TryGetValue(effectId ?? "", out var d) ? d : null;
            var maxStacks = def?.MaxStacks ?? 1;
            var policy = def?.Stacking ?? StackPolicy.Refresh;
            var effective = durationMs > 0 ? durationMs : (def?.DefaultDurationMs ?? 0);
            var expiresAt = effective <= 0 ? long.MaxValue : NowMs() + effective;

            var bag = _targets.GetOrAdd(targetKey ?? "", _ => new Dictionary<string, LiveEffect>());
            lock (bag)
            {
                if (bag.TryGetValue(effectId!, out var existing))
                {
                    switch (policy)
                    {
                        case StackPolicy.Ignore: return;
                        case StackPolicy.Stack: existing.Stacks = Math.Min(maxStacks, existing.Stacks + stacks); existing.ExpiresAtMs = expiresAt; existing.Magnitude = magnitude; existing.Source = source ?? ""; break;
                        default: existing.Stacks = Math.Max(existing.Stacks, Math.Min(maxStacks, stacks)); existing.ExpiresAtMs = expiresAt; existing.Magnitude = magnitude; existing.Source = source ?? ""; break;   // Refresh
                    }
                }
                else
                {
                    bag[effectId!] = new LiveEffect { EffectId = effectId!, Stacks = Math.Min(maxStacks, Math.Max(1, stacks)), Magnitude = magnitude, ExpiresAtMs = expiresAt, Source = source ?? "" };
                }
            }
            await Push(targetKey!).ConfigureAwait(false);
        }

        /// <summary>Removes an effect from a target (no-op if absent). Pushes the change.</summary>
        public async Task RemoveAsync(string targetKey, string effectId)
        {
            if (_targets.TryGetValue(targetKey ?? "", out var bag))
            {
                bool removed; lock (bag) removed = bag.Remove(effectId ?? "");
                if (removed) await Push(targetKey!).ConfigureAwait(false);
            }
        }

        /// <summary>Clears every effect on a target. Pushes the change.</summary>
        public async Task ClearAsync(string targetKey)
        {
            if (_targets.TryGetValue(targetKey ?? "", out var bag))
            {
                bool any; lock (bag) { any = bag.Count > 0; bag.Clear(); }
                if (any) await Push(targetKey!).ConfigureAwait(false);
            }
        }

        /// <summary>Returns a target's current effects.</summary>
        public IReadOnlyList<StatusEffect> GetAsync(string targetKey) => Snapshot(targetKey);

        private List<StatusEffect> Snapshot(string targetKey)
        {
            var now = NowMs();
            var list = new List<StatusEffect>();
            if (_targets.TryGetValue(targetKey ?? "", out var bag))
                lock (bag)
                    foreach (var e in bag.Values)
                        list.Add(new StatusEffect { EffectId = e.EffectId, Stacks = e.Stacks, Magnitude = e.Magnitude, Source = e.Source, RemainingMs = e.ExpiresAtMs == long.MaxValue ? long.MaxValue : Math.Max(0, e.ExpiresAtMs - now) });
            return list;
        }

        private async Task Tick()
        {
            var now = NowMs();
            List<string>? changed = null;
            foreach (var kv in _targets)
            {
                var bag = kv.Value;
                bool removed = false;
                lock (bag)
                {
                    List<string>? expired = null;
                    foreach (var e in bag.Values) if (e.ExpiresAtMs <= now) (expired ??= new List<string>()).Add(e.EffectId);
                    if (expired != null) { foreach (var id in expired) bag.Remove(id); removed = true; }
                }
                if (removed) (changed ??= new List<string>()).Add(kv.Key);
            }
            if (changed != null) foreach (var target in changed) await Push(target).ConfigureAwait(false);
        }

        private async Task Push(string targetKey)
        {
            var snapshot = Snapshot(targetKey);
            var payload = StatusCodec.EncodeList(0, targetKey, snapshot);

            // The affected player (if online) always sees their own effects.
            if (_online.TryGetValue(targetKey, out var owner))
                try { await owner.SendAsync(StatusEffectTypes.Event, payload, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { }

            // Plus anyone explicitly watching this target (e.g. players fighting a boss).
            if (_watchers.TryGetValue(targetKey, out var set))
            {
                List<BasePeer> peers; lock (set) peers = new List<BasePeer>(set);
                foreach (var peer in peers)
                {
                    if (ReferenceEquals(peer, _online.TryGetValue(targetKey, out var o) ? o : null)) continue;   // already pushed above
                    try { await peer.SendAsync(StatusEffectTypes.Event, payload, DeliveryMethod.Reliable).ConfigureAwait(false); } catch { }
                }
            }
        }

        internal async Task OnCommand(BasePeer peer, byte[] data)
        {
            var (corr, op, targetKey) = StatusCodec.DecodeCommand(data);
            switch (op)
            {
                case StatusOp.Watch:
                    _watchers.GetOrAdd(targetKey ?? "", _ => new HashSet<BasePeer>()).Add(peer);
                    break;
                case StatusOp.Unwatch:
                    if (_watchers.TryGetValue(targetKey ?? "", out var set)) lock (set) set.Remove(peer);
                    break;
            }
            try { await peer.SendAsync(StatusEffectTypes.Reply, StatusCodec.EncodeList(corr, targetKey, Snapshot(targetKey)), DeliveryMethod.Reliable).ConfigureAwait(false); } catch { }
        }

        /// <summary>Stops the expiry timer.</summary>
        public void Dispose() => _timer.Dispose();
    }

    /// <summary>Auto-discovered server handler for status-effect commands.</summary>
    [MessageHandler(StatusEffectTypes.Command)]
    public sealed class StatusEffectCommandHandler : IServerMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(BasePeer peer, byte[] data)
        {
            var hub = StatusEffectServer.For(peer.CurrentPeerInfo.Server);
            return hub?.OnCommand(peer, data) ?? Task.CompletedTask;
        }
    }

    /// <summary>Auto-discovered client handler for correlated status-effect replies.</summary>
    [MessageHandler(StatusEffectTypes.Reply)]
    public sealed class StatusEffectReplyHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (corr, _, effects) = StatusCodec.DecodeList(data); StatusRegistry.Complete(corr, effects); return Task.CompletedTask; }
    }

    /// <summary>Auto-discovered client handler for pushed status-effect changes.</summary>
    [MessageHandler(StatusEffectTypes.Event)]
    public sealed class StatusEffectEventHandler : IClientMessageHandler<byte[]>
    {
        /// <inheritdoc/>
        public Task HandleAsync(byte[] data) { var (_, target, effects) = StatusCodec.DecodeList(data); StatusRegistry.DispatchEvent(target, effects); return Task.CompletedTask; }
    }

    /// <summary>Attaches the status-effect hub to a server by composition.</summary>
    public static class StatusEffectServerExtensions
    {
        /// <summary>Enables the server-side status-effect hub; returns it so game logic can apply/remove effects.</summary>
        public static StatusEffectServer UseStatusEffects(this BaseServer server, StatusEffectOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return StatusEffectServer.Enable(server, options);
        }
    }

    /// <summary>Attaches a status-effect driver to a client by composition.</summary>
    public static class StatusEffectClientExtensions
    {
        /// <summary>Enables client-side status effects; returns the driver (get/watch/unwatch + <c>Changed</c>).</summary>
        public static StatusEffectClient UseStatusEffects(this BaseClient client) => new StatusEffectClient(client);
    }

    /// <summary>One-time bootstrap so the status-effect handlers are discovered. Call at startup.</summary>
    public static class StatusEffectRuntime
    {
        /// <summary>Ensures the status-effect layer is discoverable.</summary>
        public static void Enable() { _ = StatusEffectTypes.Command; }
    }
}
