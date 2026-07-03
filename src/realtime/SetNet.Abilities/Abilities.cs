using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Combat;
using SetNet.Core;
using SetNet.Protocol;
using SetNet.Stats;

namespace SetNet.Abilities
{
    /// <summary>Command operations (client → server) within the Abilities channel.</summary>
    internal enum AbilityOp : ushort { Use = 1 }

    /// <summary>Who an ability is aimed at.</summary>
    public enum TargetKind
    {
        /// <summary>No target (a global/utility effect).</summary>
        None = 0,
        /// <summary>The caster themselves.</summary>
        Self = 1,
        /// <summary>Another entity by key.</summary>
        Target = 2,
        /// <summary>A world point.</summary>
        Point = 3,
    }

    /// <summary>A simple world point for point-targeted abilities (kept local so this package needs no GeoData).</summary>
    public readonly struct AbilityPoint
    {
        /// <summary>X.</summary>
        public double X { get; }
        /// <summary>Y.</summary>
        public double Y { get; }
        /// <summary>Z.</summary>
        public double Z { get; }
        /// <summary>Creates a point.</summary>
        public AbilityPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal double DistanceTo(AbilityPoint o) { double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }

    /// <summary>Where an ability is directed: an entity key and/or a point.</summary>
    public sealed class AbilityTarget
    {
        /// <summary>The target entity key (null for self/point/none).</summary>
        public string? TargetKey { get; set; }
        /// <summary>The target point (null unless point-targeted).</summary>
        public AbilityPoint? Point { get; set; }

        /// <summary>An empty (self/none) target.</summary>
        public static readonly AbilityTarget None = new AbilityTarget();
        /// <summary>Targets an entity.</summary>
        public static AbilityTarget Of(string key) => new AbilityTarget { TargetKey = key };
        /// <summary>Targets a point.</summary>
        public static AbilityTarget At(AbilityPoint point) => new AbilityTarget { Point = point };
    }

    /// <summary>A drainable resource (mana, energy, rage…) an ability may cost.</summary>
    public sealed class ResourcePool
    {
        /// <summary>Current amount.</summary>
        public double Current { get; private set; }
        /// <summary>Maximum amount.</summary>
        public double Max { get; private set; }
        /// <summary>Creates a full pool.</summary>
        public ResourcePool(double max) { Max = max; Current = max; }
        /// <summary>Atomically spends <paramref name="amount"/>; false (unchanged) if insufficient.</summary>
        public bool TrySpend(double amount) { if (amount <= 0) return true; if (Current < amount) return false; Current -= amount; return true; }
        /// <summary>Restores up to max.</summary>
        public void Restore(double amount) { Current += amount; if (Current > Max) Current = Max; }
        /// <summary>Sets the max, clamping current.</summary>
        public void SetMax(double max) { Max = max; if (Current > Max) Current = Max; }
    }

    /// <summary>The result of trying to use an ability.</summary>
    public sealed class AbilityOutcome
    {
        /// <summary>Whether the ability fired.</summary>
        public bool Ok { get; set; }
        /// <summary>A human-readable reason (empty on success).</summary>
        public string Message { get; set; } = "";
        /// <summary>The cooldown started (ms), for the client to show.</summary>
        public int CooldownMs { get; set; }

        /// <summary>A success outcome.</summary>
        public static AbilityOutcome Success(int cooldownMs) => new AbilityOutcome { Ok = true, CooldownMs = cooldownMs };
        /// <summary>A failure outcome.</summary>
        public static AbilityOutcome Fail(string message) => new AbilityOutcome { Ok = false, Message = message };
    }

    /// <summary>The world of one ability activation, handed to each effect. Resolves caster/target stats + health via seams.</summary>
    public sealed class AbilityContext
    {
        private readonly AbilitiesServer _server;
        /// <summary>The caster's key.</summary>
        public string CasterKey { get; }
        /// <summary>The resolved target entity key (the caster for Self; the target key otherwise; may be null).</summary>
        public string? TargetKey { get; }
        /// <summary>The point, if point-targeted.</summary>
        public AbilityPoint? Point { get; }
        /// <summary>The ability being used.</summary>
        public AbilityDefinition Definition { get; }

        internal AbilityContext(AbilitiesServer server, string casterKey, string? targetKey, AbilityPoint? point, AbilityDefinition def)
        { _server = server; CasterKey = casterKey; TargetKey = targetKey; Point = point; Definition = def; }

        /// <summary>The caster's stats (or null).</summary>
        public StatSet? CasterStats => _server.StatsOf(CasterKey);
        /// <summary>The target's stats (or null).</summary>
        public StatSet? TargetStats => TargetKey != null ? _server.StatsOf(TargetKey) : null;
        /// <summary>The caster's health (or null).</summary>
        public Health? CasterHealth => _server.HealthOf(CasterKey);
        /// <summary>The target's health (or null).</summary>
        public Health? TargetHealth => TargetKey != null ? _server.HealthOf(TargetKey) : null;
        /// <summary>The shared combat resolver.</summary>
        public CombatResolver Combat => _server.Combat;
    }

    /// <summary>A composable effect applied when an ability fires (deal damage, heal, buff…). Implement your own.</summary>
    public interface IAbilityEffect
    {
        /// <summary>Applies the effect.</summary>
        Task ApplyAsync(AbilityContext ctx);
    }

    /// <summary>Deals damage from the caster's stats to the target's health via <see cref="CombatResolver"/>.</summary>
    public sealed class DamageEffect : IAbilityEffect
    {
        private readonly AttackSpec _spec;
        /// <summary>Creates a damage effect scaling the caster's attack power by <paramref name="coefficient"/>.</summary>
        public DamageEffect(double coefficient = 1.0, string damageType = "physical", double flatBonus = 0, bool canCrit = true)
            => _spec = new AttackSpec(coefficient, damageType, flatBonus) { CanCrit = canCrit };
        /// <inheritdoc/>
        public Task ApplyAsync(AbilityContext ctx)
        {
            var hp = ctx.TargetHealth;
            if (hp != null) ctx.Combat.ResolveAndApply(ctx.CasterStats, ctx.TargetStats, _spec, hp, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>Heals the target's health by a flat amount (plus an optional coefficient on a caster stat).</summary>
    public sealed class HealEffect : IAbilityEffect
    {
        private readonly double _amount;
        private readonly string? _scaleStat;
        private readonly double _coefficient;
        /// <summary>Creates a heal effect. If <paramref name="scaleStat"/> is set, adds coefficient·casterStat on top.</summary>
        public HealEffect(double amount, string? scaleStat = null, double coefficient = 0)
        { _amount = amount; _scaleStat = scaleStat; _coefficient = coefficient; }
        /// <inheritdoc/>
        public Task ApplyAsync(AbilityContext ctx)
        {
            var hp = ctx.TargetHealth;
            if (hp != null)
            {
                var amount = _amount + (_scaleStat != null ? (ctx.CasterStats?.Get(_scaleStat) ?? 0) * _coefficient : 0);
                hp.Heal(amount);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Applies stat modifiers to the target for a duration, then removes them (a timed buff/debuff).</summary>
    public sealed class BuffEffect : IAbilityEffect
    {
        private readonly IReadOnlyList<StatModifier> _mods;
        private readonly int _durationMs;
        private readonly object _sourceTag;
        /// <summary>Creates a timed buff from a set of modifiers (source tag defaults to this effect).</summary>
        public BuffEffect(IReadOnlyList<StatModifier> modifiers, int durationMs, object? sourceTag = null)
        { _mods = modifiers; _durationMs = durationMs; _sourceTag = sourceTag ?? new object(); }
        /// <inheritdoc/>
        public Task ApplyAsync(AbilityContext ctx)
        {
            var stats = ctx.TargetStats;
            if (stats == null) return Task.CompletedTask;
            // Re-tag the modifiers with a per-application source so they can be removed together.
            var applied = new List<StatModifier>(_mods.Count);
            foreach (var m in _mods) applied.Add(new StatModifier(m.StatId, m.Op, m.Value, _sourceTag));
            stats.AddModifiers(applied);
            ctx.Definition.ScheduleRemoval(stats, _sourceTag, _durationMs);
            return Task.CompletedTask;
        }
    }

    /// <summary>An ability: its gating (cooldown/cost/range/target) and the effects it applies. Build with object/collection initializers.</summary>
    public sealed class AbilityDefinition
    {
        /// <summary>Unique ability id.</summary>
        public string Id { get; set; } = "";
        /// <summary>Cooldown in milliseconds.</summary>
        public int CooldownMs { get; set; }
        /// <summary>Advisory cast time (ms) for the client's cast bar (v1 applies instantly; use the cooldown to gate spam).</summary>
        public int CastTimeMs { get; set; }
        /// <summary>Max range from caster to target (0 = unlimited / self).</summary>
        public double Range { get; set; }
        /// <summary>Optional resource id spent to cast (mapped via <see cref="AbilityOptions.ResourceOf"/>).</summary>
        public string? ResourceId { get; set; }
        /// <summary>Amount of the resource spent.</summary>
        public double ResourceCost { get; set; }
        /// <summary>Who the ability targets.</summary>
        public TargetKind TargetKind { get; set; } = TargetKind.Target;
        /// <summary>The effects applied when it fires.</summary>
        public List<IAbilityEffect> Effects { get; } = new List<IAbilityEffect>();

        // Buff-removal timers keep a strong ref until they fire.
        private readonly ConcurrentDictionary<object, Timer> _buffTimers = new ConcurrentDictionary<object, Timer>();
        internal void ScheduleRemoval(StatSet stats, object sourceTag, int durationMs)
        {
            if (durationMs <= 0) { stats.RemoveBySource(sourceTag); return; }
            var timer = new Timer(_ =>
            {
                try { stats.RemoveBySource(sourceTag); } catch { }
                if (_buffTimers.TryRemove(sourceTag, out var t)) t.Dispose();
            }, null, durationMs, Timeout.Infinite);
            _buffTimers[sourceTag] = timer;
        }
    }

    /// <summary>Seams the abilities hub uses to reach entity state — the same for players and mobs.</summary>
    public sealed class AbilityOptions
    {
        /// <summary>Resolves an entity key to its stats (or null).</summary>
        public Func<string, StatSet?>? StatsOf { get; set; }
        /// <summary>Resolves an entity key to its health pool (or null).</summary>
        public Func<string, Health?>? HealthOf { get; set; }
        /// <summary>Resolves an entity key to its world position (for range checks; null disables range enforcement).</summary>
        public Func<string, AbilityPoint?>? PositionOf { get; set; }
        /// <summary>Resolves (entityKey, resourceId) to a resource pool (or null = the ability has no cost gate).</summary>
        public Func<string, string, ResourcePool?>? ResourceOf { get; set; }
        /// <summary>The combat resolver used by damage effects (default new).</summary>
        public CombatResolver Combat { get; set; } = new CombatResolver();
        /// <summary>Maps a connected peer to its stable player key (used as the caster).</summary>
        public Func<BasePeer, string> PlayerKey { get; set; } = peer => peer.CurrentPeerInfo.Id.ToString();
    }

    // ---- wire ----

    internal static class AbilityCodec
    {
        public static byte[] EncodeUse(string abilityId, AbilityTarget target)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(abilityId ?? "");
            w.Write(target.TargetKey ?? "");
            w.Write(target.Point.HasValue);
            if (target.Point.HasValue) { w.Write(target.Point.Value.X); w.Write(target.Point.Value.Y); w.Write(target.Point.Value.Z); }
            return ms.ToArray();
        }

        public static (string abilityId, AbilityTarget target) DecodeUse(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var id = r.ReadString();
            var key = r.ReadString();
            var hasPoint = r.ReadBoolean();
            var target = new AbilityTarget { TargetKey = string.IsNullOrEmpty(key) ? null : key };
            if (hasPoint) target.Point = new AbilityPoint(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            return (id, target);
        }

        public static byte[] EncodeOutcome(AbilityOutcome o)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(o.Ok); w.Write(o.Message ?? ""); w.Write(o.CooldownMs);
            return ms.ToArray();
        }

        public static AbilityOutcome DecodeOutcome(byte[] body)
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            return new AbilityOutcome { Ok = r.ReadBoolean(), Message = r.ReadString(), CooldownMs = r.ReadInt32() };
        }
    }

    // ---- client ----

    /// <summary>Client-side abilities driver (from <see cref="AbilitiesClientExtensions.UseAbilities"/>).</summary>
    public sealed class AbilitiesClient
    {
        private readonly BaseClient _client;
        internal AbilitiesClient(BaseClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>Requests to use an ability on an optional target/point. The server validates and applies.</summary>
        public async Task<AbilityOutcome> UseAsync(string abilityId, string? targetKey = null, AbilityPoint? point = null)
        {
            var target = new AbilityTarget { TargetKey = targetKey, Point = point };
            try
            {
                var body = await _client.RequestRawAsync(Channels.Abilities, (ushort)AbilityOp.Use, AbilityCodec.EncodeUse(abilityId, target)).ConfigureAwait(false);
                return AbilityCodec.DecodeOutcome(body);
            }
            catch (ProtocolException ex) { return AbilityOutcome.Fail(ex.Message); }
            catch (TimeoutException) { return AbilityOutcome.Fail("ability use timed out"); }
        }
    }

    // ---- server ----

    /// <summary>
    /// Server-side abilities hub (from <see cref="AbilitiesServerExtensions.UseAbilities"/>): the authority for cooldowns,
    /// costs, range and effects. Both client requests and game/mob logic go through <see cref="TryUseAsync"/>.
    /// </summary>
    public sealed class AbilitiesServer
    {
        private static readonly ConcurrentDictionary<BaseServer, AbilitiesServer> Servers = new ConcurrentDictionary<BaseServer, AbilitiesServer>();
        private static readonly double TicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static double NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * TicksToMs;

        private readonly AbilityOptions _options;
        private readonly ConcurrentDictionary<string, AbilityDefinition> _defs = new ConcurrentDictionary<string, AbilityDefinition>();
        private readonly ConcurrentDictionary<(string caster, string ability), double> _cooldowns = new ConcurrentDictionary<(string, string), double>();

        internal AbilitiesServer(AbilityOptions options) => _options = options;
        internal static AbilitiesServer Enable(BaseServer server, AbilityOptions options) => Servers.GetOrAdd(server, _ => new AbilitiesServer(options));
        internal static AbilitiesServer? For(BaseServer? server) => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <summary>The combat resolver damage effects use.</summary>
        public CombatResolver Combat => _options.Combat;
        internal StatSet? StatsOf(string key) => _options.StatsOf?.Invoke(key);
        internal Health? HealthOf(string key) => _options.HealthOf?.Invoke(key);

        /// <summary>Registers an ability.</summary>
        public AbilitiesServer Define(AbilityDefinition ability)
        {
            _defs[ability.Id] = ability ?? throw new ArgumentNullException(nameof(ability));
            return this;
        }

        /// <summary>Milliseconds of cooldown remaining for a caster's ability (0 if ready).</summary>
        public int CooldownRemaining(string casterKey, string abilityId)
        {
            if (_cooldowns.TryGetValue((casterKey, abilityId), out var due))
            {
                var rem = due - NowMs();
                if (rem > 0) return (int)rem;
            }
            return 0;
        }

        /// <summary>
        /// The single entry point: validates cooldown, cost, target and range, applies the ability's effects, starts its
        /// cooldown, and returns the outcome. Call it from a client request or directly from server/mob logic.
        /// </summary>
        public async Task<AbilityOutcome> TryUseAsync(string casterKey, string abilityId, AbilityTarget? target = null)
        {
            if (!_defs.TryGetValue(abilityId, out var def)) return AbilityOutcome.Fail($"unknown ability '{abilityId}'");
            target ??= AbilityTarget.None;

            if (CooldownRemaining(casterKey, abilityId) > 0) return AbilityOutcome.Fail("on cooldown");

            // Resolve the target entity key from the target kind.
            string? targetKey = def.TargetKind switch
            {
                TargetKind.Self => casterKey,
                TargetKind.Target => target.TargetKey,
                _ => null,
            };
            if (def.TargetKind == TargetKind.Target && string.IsNullOrEmpty(targetKey)) return AbilityOutcome.Fail("no target");

            // Range check (only if we can resolve positions and there's a target entity or point).
            if (def.Range > 0 && _options.PositionOf != null)
            {
                var from = _options.PositionOf(casterKey);
                AbilityPoint? to = target.Point ?? (targetKey != null ? _options.PositionOf(targetKey) : null);
                if (from.HasValue && to.HasValue && from.Value.DistanceTo(to.Value) > def.Range) return AbilityOutcome.Fail("out of range");
            }

            // Resource cost.
            if (!string.IsNullOrEmpty(def.ResourceId) && def.ResourceCost > 0 && _options.ResourceOf != null)
            {
                var pool = _options.ResourceOf(casterKey, def.ResourceId!);
                if (pool != null && !pool.TrySpend(def.ResourceCost)) return AbilityOutcome.Fail("not enough " + def.ResourceId);
            }

            // Start the cooldown BEFORE applying (so a throwing effect can't be spammed), then apply effects.
            _cooldowns[(casterKey, abilityId)] = NowMs() + def.CooldownMs;
            var ctx = new AbilityContext(this, casterKey, targetKey, target.Point, def);
            foreach (var effect in def.Effects)
                try { await effect.ApplyAsync(ctx).ConfigureAwait(false); } catch { /* one bad effect shouldn't abort the rest */ }

            return AbilityOutcome.Success(def.CooldownMs);
        }

        internal async Task HandleUseAsync(ChannelRequest request)
        {
            var (abilityId, target) = AbilityCodec.DecodeUse(request.RawBody);
            var caster = _options.PlayerKey(request.Peer);
            var outcome = await TryUseAsync(caster, abilityId, target).ConfigureAwait(false);
            await request.ReplyRawAsync(AbilityCodec.EncodeOutcome(outcome)).ConfigureAwait(false);
        }
    }

    /// <summary>Auto-discovered channel service for ability use.</summary>
    [ProtocolChannel(Channels.Abilities)]
    public sealed class AbilitiesChannelService : IChannelService
    {
        /// <inheritdoc/>
        public Task HandleAsync(ChannelRequest request)
        {
            var hub = AbilitiesServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("abilities are not configured on this server");
            if (request.Op != (ushort)AbilityOp.Use) throw new ProtocolException($"unknown abilities op {request.Op}");
            return hub.HandleUseAsync(request);
        }
    }

    /// <summary>Attaches the abilities hub to a server.</summary>
    public static class AbilitiesServerExtensions
    {
        /// <summary>Enables the server-side abilities hub; returns it so you can <c>Define</c> abilities and <c>TryUse</c> them.</summary>
        public static AbilitiesServer UseAbilities(this BaseServer server, AbilityOptions options)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return AbilitiesServer.Enable(server, options);
        }
    }

    /// <summary>Attaches an abilities driver to a client.</summary>
    public static class AbilitiesClientExtensions
    {
        /// <summary>Enables client-side abilities; returns the driver (<c>UseAsync</c>).</summary>
        public static AbilitiesClient UseAbilities(this BaseClient client) => new AbilitiesClient(client);
    }

    /// <summary>One-time bootstrap so the abilities channel service is discovered. Call at startup.</summary>
    public static class AbilitiesRuntime
    {
        /// <summary>Ensures the abilities layer is discoverable.</summary>
        public static void Enable() { _ = typeof(AbilitiesChannelService); }
    }
}
