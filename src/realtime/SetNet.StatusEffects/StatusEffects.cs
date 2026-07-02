using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.Protocol;

namespace SetNet.StatusEffects
{
    /// <summary>Command operations (client → server) within the StatusEffects protocol channel.</summary>
    internal enum StatusOp : ushort { Watch = 1, Unwatch = 2, Get = 3 }

    /// <summary>Push events (server → client) within the StatusEffects protocol channel.</summary>
    internal enum StatusEvt : ushort { Changed = 10 }

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

    /// <summary>Body codecs for the StatusEffects channel (payload only; op/correlation are in the envelope).</summary>
    internal static class StatusCodec
    {
        public static byte[] EncodeEffects(IReadOnlyList<StatusEffect> effects)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(effects.Count);
            foreach (var e in effects) { w.Write(e.EffectId ?? ""); w.Write(e.Stacks); w.Write(e.Magnitude); w.Write(e.RemainingMs); w.Write(e.Source ?? ""); }
            return ms.ToArray();
        }

        public static List<StatusEffect> DecodeEffects(BinaryReader r)
        {
            var count = r.ReadInt32();
            var list = new List<StatusEffect>(count);
            for (var i = 0; i < count; i++)
                list.Add(new StatusEffect { EffectId = r.ReadString(), Stacks = r.ReadInt32(), Magnitude = r.ReadDouble(), RemainingMs = r.ReadInt64(), Source = r.ReadString() });
            return list;
        }

        public static List<StatusEffect> DecodeEffects(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<StatusEffect>();
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return DecodeEffects(r);
        }

        public static byte[] EncodeChanged(string targetKey, IReadOnlyList<StatusEffect> effects)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(targetKey ?? "");
            w.Write(effects.Count);
            foreach (var e in effects) { w.Write(e.EffectId ?? ""); w.Write(e.Stacks); w.Write(e.Magnitude); w.Write(e.RemainingMs); w.Write(e.Source ?? ""); }
            return ms.ToArray();
        }

        public static (string TargetKey, List<StatusEffect> Effects) DecodeChanged(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var target = r.ReadString();
            return (target, DecodeEffects(r));
        }

        public static byte[] EncodeCommand(string targetKey)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(targetKey ?? "");
            return ms.ToArray();
        }

        public static string DecodeCommand(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return r.ReadString();
        }
    }

    /// <summary>
    /// Client-side status-effect driver, attached by <see cref="StatusEffectClientExtensions.UseStatusEffects"/>.
    /// Read the effects on any target key and <see cref="WatchAsync"/> a target (your own buffs, or an enemy's
    /// debuffs) to receive live updates via <see cref="Changed"/>. Effects are applied by server game logic; the
    /// client only observes. Rides the unified protocol on the <see cref="Channels.StatusEffects"/> channel.
    /// </summary>
    public sealed class StatusEffectClient
    {
        private readonly BaseClient _client;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        /// <summary>Raised when a watched target's effect list changes (args: target key, current effects).</summary>
        public event Action<string, IReadOnlyList<StatusEffect>>? Changed;

        internal StatusEffectClient(BaseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _subscriptions.Add(_client.OnRaw(Channels.StatusEffects, (ushort)StatusEvt.Changed, OnChanged));
        }

        /// <summary>Fetches a target's current effects.</summary>
        public Task<IReadOnlyList<StatusEffect>> GetAsync(string targetKey) => Send(StatusOp.Get, targetKey);

        /// <summary>Starts watching a target; you'll get its current effects now and a <see cref="Changed"/> on every update.</summary>
        public Task<IReadOnlyList<StatusEffect>> WatchAsync(string targetKey) => Send(StatusOp.Watch, targetKey);

        /// <summary>Stops watching a target.</summary>
        public Task UnwatchAsync(string targetKey) => Send(StatusOp.Unwatch, targetKey);

        private async Task<IReadOnlyList<StatusEffect>> Send(StatusOp op, string targetKey)
        {
            try
            {
                var body = await _client.RequestRawAsync(Channels.StatusEffects, (ushort)op, StatusCodec.EncodeCommand(targetKey)).ConfigureAwait(false);
                return StatusCodec.DecodeEffects(body);
            }
            catch (ProtocolException ex) { throw new StatusEffectException(ex.Message); }
            catch (TimeoutException) { throw new StatusEffectException("Status-effect command timed out."); }
        }

        private void OnChanged(byte[] body)
        {
            var (target, effects) = StatusCodec.DecodeChanged(body);
            Changed?.Invoke(target, effects);
        }
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
            var payload = StatusCodec.EncodeChanged(targetKey, snapshot);

            // The affected player (if online) always sees their own effects.
            if (_online.TryGetValue(targetKey, out var owner))
                try { await owner.PublishRawAsync(Channels.StatusEffects, (ushort)StatusEvt.Changed, payload).ConfigureAwait(false); } catch { }

            // Plus anyone explicitly watching this target (e.g. players fighting a boss).
            if (_watchers.TryGetValue(targetKey, out var set))
            {
                List<BasePeer> peers; lock (set) peers = new List<BasePeer>(set);
                foreach (var peer in peers)
                {
                    if (ReferenceEquals(peer, _online.TryGetValue(targetKey, out var o) ? o : null)) continue;   // already pushed above
                    try { await peer.PublishRawAsync(Channels.StatusEffects, (ushort)StatusEvt.Changed, payload).ConfigureAwait(false); } catch { }
                }
            }
        }

        internal async Task HandleAsync(ChannelRequest request)
        {
            var targetKey = StatusCodec.DecodeCommand(request.RawBody) ?? "";
            switch ((StatusOp)request.Op)
            {
                case StatusOp.Watch:
                    _watchers.GetOrAdd(targetKey, _ => new HashSet<BasePeer>()).Add(request.Peer);
                    break;
                case StatusOp.Unwatch:
                    if (_watchers.TryGetValue(targetKey, out var set)) lock (set) set.Remove(request.Peer);
                    break;
            }
            await request.ReplyRawAsync(StatusCodec.EncodeEffects(Snapshot(targetKey))).ConfigureAwait(false);
        }

        /// <summary>Stops the expiry timer.</summary>
        public void Dispose() => _timer.Dispose();
    }

    // ---- auto-discovered channel service ----

    /// <summary>Auto-discovered channel service for status-effect commands.</summary>
    [ProtocolChannel(Channels.StatusEffects)]
    public sealed class StatusEffectChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = StatusEffectServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("status effects is not configured on this server");
            return hub.HandleAsync(request);
        }
    }

    // ---- composition entry points ----

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

    /// <summary>One-time bootstrap so the status-effect channel service is discovered. Call at startup.</summary>
    public static class StatusEffectRuntime
    {
        /// <summary>Ensures the status-effect layer is discoverable.</summary>
        public static void Enable() { _ = typeof(StatusEffectChannelService); }
    }
}
