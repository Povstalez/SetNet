using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SetNet.Core;
using SetNet.GeoData;
using SetNet.PathFinding;
using SetNet.Protocol;

namespace SetNet.Mobs
{
    /// <summary>
    /// The server-authoritative hub for hostile AI entities, attached by <see cref="MobServerExtensions.UseMobs"/>.
    /// Owns every mob's state and runs the AI: a fixed-rate tick builds each awake mob's <see cref="MobSenses"/>, calls
    /// its brain's <c>ThinkAsync</c>, advances movement (via a pathfinder or straight-line) and casts, and resolves
    /// abilities/damage/death. Reactive damage flows off the combat path (client attack → <c>OnDamagedAsync</c>). Mobs
    /// with no players in interest range are skipped ("sleep when unobserved"). Replication is a seam: the default
    /// no-op leaves you to read <see cref="Mobs"/> / handle <see cref="MobMoved"/>, or plug an <see cref="IMobReplication"/>.
    /// </summary>
    public sealed class MobServer : IDisposable, SetNet.Ticks.IAsyncTickable
    {
        private static readonly ConcurrentDictionary<BaseServer, MobServer> Servers
            = new ConcurrentDictionary<BaseServer, MobServer>();

        private readonly BaseServer _server;
        private readonly MobOptions _options;
        private readonly IPlayerPositions _positions;
        private readonly IPathfinder? _pathfinder;
        private readonly IServiceProvider _services;
        private readonly IDamageSink _damageSink;

        private readonly ConcurrentDictionary<string, IMobBrain> _brains = new ConcurrentDictionary<string, IMobBrain>();
        private readonly ConcurrentDictionary<string, MobRuntime> _mobs = new ConcurrentDictionary<string, MobRuntime>();
        private readonly ConcurrentDictionary<string, BasePeer> _online = new ConcurrentDictionary<string, BasePeer>();

        private long _nextId;
        private int _ticking;
        private Timer? _timer;
        private readonly IDisposable? _tickReg;

        /// <summary>Raised (on the tick) when a mob's position changes — for apps replicating without an adapter.</summary>
        public event Action<MobInstance>? MobMoved;

        /// <summary>Raised each tick after a mob's state has been advanced — a superset cue for custom replication.</summary>
        public event Action<MobInstance>? MobUpdated;

        private MobServer(BaseServer server, MobOptions options)
        {
            _server = server;
            _options = options;
            _positions = ResolvePositions(options);
            _pathfinder = options.Pathfinder ?? TryBuildPathfinder(options.GeoData);
            _services = options.Services ?? EmptyServices.Instance;
            _damageSink = ResolveDamageSink(_services);

            server.PeerConnected += peer => _online[options.PlayerKey(peer)] = peer;
            server.PeerDisconnected += peer =>
            {
                var key = options.PlayerKey(peer);
                if (_online.TryGetValue(key, out var cur) && ReferenceEquals(cur, peer)) _online.TryRemove(key, out _);
            };

            // Prefer the ambient tick host (auto-subscribe, one place drives everything); fall back to the internal timer.
            if (options.AutoTick && SetNet.Ticks.TickHost.Current is { } host)
            {
                _tickReg = host.Register((SetNet.Ticks.IAsyncTickable)this, options.TickChannel, options.TickRateHz, options.TickPriority);
            }
            else if (options.UseInternalTimer)
            {
                var period = Math.Max(1, 1000 / Math.Max(1, options.TickRateHz));
                _timer = new Timer(_ => TickFromTimer(), null, period, period);
            }
        }

        // ---- registration / spawn / despawn ----

        /// <summary>Registers the brain for a mob type. Overwrites any brain already registered for the same type.</summary>
        public void Register(IMobBrain brain)
        {
            if (brain == null) throw new ArgumentNullException(nameof(brain));
            _brains[brain.MobType] = brain;
        }

        /// <summary>Spawns a mob and returns its id. Runs the brain's <c>OnSpawn</c> and notifies the replication seam.</summary>
        public string Spawn(MobSpawn spawn)
        {
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            if (!_brains.TryGetValue(spawn.Type, out var brain))
                throw new InvalidOperationException($"No brain registered for mob type '{spawn.Type}'. Call Register first.");

            var id = "mob-" + Interlocked.Increment(ref _nextId).ToString();
            var mob = new MobInstance
            {
                Id = id,
                Type = spawn.Type,
                Position = spawn.Position,
                SpawnPoint = spawn.Position,
                Zone = spawn.Zone,
                Health = spawn.Health,
                MaxHealth = spawn.Health,
                Faction = spawn.Faction,
            };
            var runtime = new MobRuntime(mob, brain, spawn.RespawnMs);
            _mobs[id] = runtime;
            _options.Mover?.OnSpawn(mob, _options.MoveSpeed);

            var ctx = new Ctx(this, runtime);
            try { brain.OnSpawn(ctx); } catch { /* brain isolation */ }

            _options.Replication.OnMobSpawned(mob);
            _ = PublishSpawnAsync(mob);
            return id;
        }

        /// <summary>Removes a mob immediately (no death event). Notifies clients and the replication seam.</summary>
        public void Despawn(string mobId)
        {
            if (mobId == null || !_mobs.TryRemove(mobId, out var removed)) return;
            _options.Mover?.OnDespawn(removed.Mob);
            _options.Replication.OnMobDespawned(mobId);
            _ = PublishToInterestedAsync((ushort)MobsEvt.MobDespawned, MobsWire.EncodeId(mobId), null);
        }

        /// <summary>A snapshot of the live mobs (for polling apps / diagnostics).</summary>
        public IReadOnlyCollection<MobInstance> Mobs
        {
            get { var list = new List<MobInstance>(_mobs.Count); foreach (var r in _mobs.Values) list.Add(r.Mob); return list; }
        }

        /// <summary>The mob with this id, or null.</summary>
        public MobInstance? Get(string mobId) => _mobs.TryGetValue(mobId, out var r) ? r.Mob : null;

        /// <summary>The number of mobs that ran their brain on the last tick (awake); useful for tuning.</summary>
        public int AwakeCount { get; private set; }

        // ---- tick ----

        private long _timerLast;
        private void TickFromTimer()
        {
            var now = NowMs();
            var last = Interlocked.Exchange(ref _timerLast, now);
            var dt = last == 0 ? (1000.0 / Math.Max(1, _options.TickRateHz)) : (now - last);
            _ = TickAsync(dt);
        }

        /// <summary>
        /// Manually advances the AI by <paramref name="dtMs"/> milliseconds. Use this when
        /// <see cref="MobOptions.UseInternalTimer"/> is false to drive mobs from your own game loop. Safe to call from a
        /// single loop; overlapping calls are skipped.
        /// </summary>
        public Task Update(double dtMs) => TickAsync(dtMs);

        /// <summary>Lets a <c>SetNet.Ticks.TickScheduler</c> drive mob AI: <c>channel.Add(mobs)</c>. (Overlaps are self-guarded.)</summary>
        Task SetNet.Ticks.IAsyncTickable.TickAsync(SetNet.Ticks.TickInfo tick) => Update(tick.DeltaMs);

        private async Task TickAsync(double dtMs)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;   // never overlap ticks
            try
            {
                var awake = 0;
                foreach (var runtime in _mobs.Values)
                {
                    var mob = runtime.Mob;
                    if (!mob.IsAlive) continue;

                    // Sleep when unobserved: no players in interest range → skip Think (still let a pending respawn run).
                    if (!HasInterest(mob)) continue;
                    awake++;

                    try
                    {
                        var senses = BuildSenses(runtime);
                        DetectTargetLost(runtime, senses);
                        var ctx = new Ctx(this, runtime);
                        await runtime.Brain.ThinkAsync(ctx, senses).ConfigureAwait(false);
                    }
                    catch { /* brain isolation — one bad tick must not stall the loop */ }

                    AdvanceMovement(runtime, dtMs);
                    await AdvanceCastAsync(runtime, dtMs).ConfigureAwait(false);

                    _options.Replication.OnMobUpdated(mob);
                    RaiseUpdated(mob);
                }
                AwakeCount = awake;
            }
            catch { /* never throw on the timer thread */ }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        private void DetectTargetLost(MobRuntime runtime, MobSenses senses)
        {
            var mob = runtime.Mob;
            if (mob.Target == null) return;
            if (senses.Target == null || !senses.InLeashRange)
            {
                var lost = mob.Target;
                mob.Threat.Remove(lost);
                var ctx = new Ctx(this, runtime);
                try { _ = runtime.Brain.OnTargetLostAsync(ctx); } catch { /* isolation */ }
                // The brain typically clears the target in OnTargetLost; ensure it's cleared even if it doesn't.
                if (ReferenceEquals(mob.Target, lost)) mob.Target = null;
            }
        }

        private void AdvanceMovement(MobRuntime runtime, double dtMs)
        {
            var mob = runtime.Mob;

            // Delegated movement: an external mover (e.g. SetNet.Mobs.Locomotion) owns the position stepping. We only
            // push the goal when it changes and read the advanced position back — the AI/perception/combat is unchanged.
            if (_options.Mover != null)
            {
                if (runtime.MoveGoal is { } g)
                {
                    if (!runtime.MoverActive || Vec3.DistanceSquared(runtime.PathGoal, g) > 0.25f)
                    {
                        _options.Mover.SetGoal(mob, g);
                        runtime.PathGoal = g;
                        runtime.MoverActive = true;
                    }
                }
                else if (runtime.MoverActive)
                {
                    _options.Mover.Stop(mob);
                    runtime.MoverActive = false;
                }

                var was = mob.Position;
                mob.Position = _options.Mover.Position(mob);
                mob.Velocity = dtMs > 0 ? (mob.Position - was) * (float)(1000.0 / dtMs) : Vec3.Zero;
                if (Vec3.DistanceSquared(was, mob.Position) > 1e-6f) RaiseMoved(mob);
                return;
            }

            if (runtime.MoveGoal == null) { mob.Velocity = Vec3.Zero; return; }

            var goal = runtime.MoveGoal.Value;
            var maxStep = _options.MoveSpeed * (float)(dtMs / 1000.0);
            if (maxStep <= 0) return;

            // (Re)compute a path when the goal changes materially and a pathfinder exists.
            if (_pathfinder != null)
            {
                if (runtime.Follower == null || Vec3.DistanceSquared(runtime.PathGoal, goal) > 0.25f)
                {
                    var path = _pathfinder.FindPath(mob.Position, goal);
                    runtime.Follower = path.IsEmpty ? null : new PathFollower(path);
                    runtime.PathGoal = goal;
                }
                if (runtime.Follower != null && !runtime.Follower.Arrived)
                {
                    var before = mob.Position;
                    var next = runtime.Follower.Step(before, maxStep);
                    mob.Velocity = dtMs > 0 ? (next - before) * (float)(1000.0 / dtMs) : Vec3.Zero;
                    mob.Position = next;
                    if (Vec3.DistanceSquared(before, next) > 1e-6f) RaiseMoved(mob);
                    return;
                }
            }

            // Straight-line fallback (no pathfinder, or path failed).
            StraightLineStep(mob, goal, maxStep, dtMs);
        }

        private void StraightLineStep(MobInstance mob, Vec3 goal, float maxStep, double dtMs)
        {
            var to = goal - mob.Position;
            var dist = to.Length;
            if (dist <= 1e-4f) { mob.Velocity = Vec3.Zero; return; }
            var before = mob.Position;
            var step = Math.Min(maxStep, dist);
            var next = mob.Position + to.Normalized * step;
            mob.Velocity = dtMs > 0 ? (next - before) * (float)(1000.0 / dtMs) : Vec3.Zero;
            mob.Position = next;
            RaiseMoved(mob);
        }

        private async Task AdvanceCastAsync(MobRuntime runtime, double dtMs)
        {
            var cast = runtime.Mob.Casting;
            if (cast == null) return;
            cast.RemainingMs -= (int)dtMs;
            if (cast.RemainingMs > 0) return;

            // Cast complete: resolve it.
            runtime.Mob.Casting = null;
            await ResolveAbilityAsync(runtime, cast.AbilityId, cast.TargetKey).ConfigureAwait(false);
        }

        // ---- abilities / damage ----

        private bool TryBeginAbility(MobRuntime runtime, string abilityId, string targetKey, out MobAbility ability)
        {
            ability = null!;
            var mob = runtime.Mob;
            if (mob.Casting != null) return false;                         // already casting
            if (!_options.Abilities.TryGetValue(abilityId, out ability)) return false;   // unknown ability
            if (!runtime.IsOffCooldown(abilityId, NowMs())) return false;  // on cooldown

            // Range check against the resolved target (self-targeted abilities skip it).
            if (!IsSelf(mob, targetKey))
            {
                var tpos = _positions.PositionOf(targetKey);
                if (tpos == null) return false;
                if (Vec3.Distance(mob.Position, tpos.Value) > ability.Range) return false;   // out of range
            }
            return true;
        }

        private async Task StartAbilityAsync(MobRuntime runtime, string abilityId, string targetKey)
        {
            if (!TryBeginAbility(runtime, abilityId, targetKey, out var ability)) return;
            runtime.SetOnCooldown(abilityId, NowMs() + ability.CooldownMs);

            if (ability.CastTimeMs > 0)
            {
                // Telegraphed cast: resolves when the timer runs the cast down.
                runtime.Mob.Casting = new MobCastState
                {
                    AbilityId = abilityId, TargetKey = targetKey,
                    TotalMs = ability.CastTimeMs, RemainingMs = ability.CastTimeMs,
                };
                return;
            }
            await ResolveAbilityAsync(runtime, abilityId, targetKey).ConfigureAwait(false);
        }

        private async Task ResolveAbilityAsync(MobRuntime runtime, string abilityId, string targetKey)
        {
            if (!_options.Abilities.TryGetValue(abilityId, out var ability)) return;
            var mob = runtime.Mob;

            var targets = ResolveTargets(mob, ability, targetKey);
            foreach (var t in targets)
            {
                try { await _damageSink.ApplyToPlayerAsync(t, mob.Id, abilityId, ability.Damage, ability.EffectId).ConfigureAwait(false); }
                catch { /* app sink isolation */ }
            }

            await PublishToInterestedAsync((ushort)MobsEvt.MobAttack,
                MobsWire.EncodeAttackEvent(mob.Id, abilityId, ability.Damage, targets), null).ConfigureAwait(false);
        }

        private List<string> ResolveTargets(MobInstance mob, MobAbility ability, string targetKey)
        {
            var result = new List<string>();
            if (IsSelf(mob, targetKey)) return result;   // self-cast (e.g. heal) — no damage targets

            if (ability.AoeRadius <= 0)
            {
                if (!string.IsNullOrEmpty(targetKey)) result.Add(targetKey);
                return result;
            }

            // AoE centred on the resolved target's position.
            var center = _positions.PositionOf(targetKey);
            if (center == null) { if (!string.IsNullOrEmpty(targetKey)) result.Add(targetKey); return result; }
            var r2 = ability.AoeRadius * ability.AoeRadius;
            foreach (var key in _positions.AllPlayers())
            {
                var pos = _positions.PositionOf(key);
                if (pos == null) continue;
                if (Vec3.DistanceSquared(center.Value, pos.Value) <= r2) result.Add(key);
            }
            return result;
        }

        /// <summary>Applies player-dealt damage to a mob (validated attack path): subtracts HP, adds threat, fires the reactive hook, handles death.</summary>
        internal async Task<(bool ok, string reason)> PlayerAttackAsync(BasePeer peer, string mobId, string abilityId)
        {
            if (!_mobs.TryGetValue(mobId, out var runtime) || !runtime.Mob.IsAlive) return (false, "mob not found");
            if (!_options.Abilities.TryGetValue(abilityId, out var ability)) return (false, "unknown ability");

            var mob = runtime.Mob;
            var playerKey = _options.PlayerKey(peer);

            var ppos = _positions.PositionOf(playerKey);
            if (ppos == null) return (false, "attacker position unknown");
            if (Vec3.Distance(ppos.Value, mob.Position) > ability.Range) return (false, "out of range");
            if (!runtime.IsPlayerAttackReady(playerKey, abilityId, NowMs(), ability.CooldownMs)) return (false, "on cooldown");
            runtime.MarkPlayerAttack(playerKey, abilityId, NowMs());

            var wasAlive = mob.IsAlive;
            mob.Health -= ability.Damage;
            mob.Threat.Add(playerKey, (float)(ability.Damage * _options.ThreatPerDamage));

            var ctx = new Ctx(this, runtime);
            try { await runtime.Brain.OnDamagedAsync(ctx, new DamageEvent(playerKey, ability.Damage, abilityId)).ConfigureAwait(false); }
            catch { /* brain isolation */ }

            _options.Replication.OnMobUpdated(mob);
            RaiseUpdated(mob);

            if (wasAlive && !mob.IsAlive) await KillAsync(runtime, playerKey).ConfigureAwait(false);
            return (true, "");
        }

        private async Task KillAsync(MobRuntime runtime, string? killerKey)
        {
            var mob = runtime.Mob;
            mob.Health = 0;
            mob.Casting = null;

            var ctx = new Ctx(this, runtime);
            try { await runtime.Brain.OnDeathAsync(ctx, killerKey).ConfigureAwait(false); }
            catch { /* brain isolation */ }

            // Optional loot/xp sinks from Services (best-effort, app-owned).
            (_services.GetService(typeof(IMobLootSink)) as IMobLootSink)?.OnMobKilled(mob, killerKey);

            await PublishToInterestedAsync((ushort)MobsEvt.MobDeath,
                MobsWire.EncodeDeath(mob.Id, killerKey, mob.Position), null).ConfigureAwait(false);

            _mobs.TryRemove(mob.Id, out _);
            _options.Mover?.OnDespawn(mob);
            _options.Replication.OnMobDespawned(mob.Id);

            if (runtime.RespawnMs > 0)
            {
                var spawn = new MobSpawn
                {
                    Type = mob.Type, Position = mob.SpawnPoint, Zone = mob.Zone,
                    Health = mob.MaxHealth, Faction = mob.Faction, RespawnMs = runtime.RespawnMs,
                };
                _ = ScheduleRespawnAsync(spawn, runtime.RespawnMs);
            }
        }

        private async Task ScheduleRespawnAsync(MobSpawn spawn, int delayMs)
        {
            try { await Task.Delay(delayMs).ConfigureAwait(false); Spawn(spawn); } catch { /* teardown */ }
        }

        // ---- perception / interest ----

        private MobSenses BuildSenses(MobRuntime runtime)
        {
            var mob = runtime.Mob;
            var (radius, requireLos, leash) = PerceptionParamsFor(runtime.Brain);
            var nearby = _options.Perception.Perceive(mob, _positions, _options.GeoData, radius, requireLos);

            PerceivedPlayer? target = null;
            if (mob.Target != null)
                foreach (var p in nearby) if (p.PlayerKey == mob.Target) { target = p; break; }

            var inLeash = Vec3.Distance(mob.Position, mob.SpawnPoint) <= leash;
            return new MobSenses(nearby, target, inLeash, mob.HealthFraction);
        }

        // Read the perception window from a known brain type; composed brains expose it; others use defaults.
        private (float radius, bool requireLos, float leash) PerceptionParamsFor(IMobBrain brain)
        {
            switch (brain)
            {
                case ComposedBrain c: return (c.AggroRadius, c.RequireLos, c.LeashRadius);
                case AggressiveBrain a: return (a.AggroRadius, a.RequireLos, a.LeashRadius);
                case PassiveRetaliateBrain p: return (p.AggroRadius, p.RequireLos, p.LeashRadius);
                case RangedBrain r: return (r.AggroRadius, r.RequireLos, r.LeashRadius);
                case CasterBrain cb: return (cb.AggroRadius, cb.RequireLos, cb.LeashRadius);
                default: return (12f, true, 25f);
            }
        }

        // A mob has interest if any player is within its aggro radius (cheap gate for sleep-when-unobserved).
        // Uses the same player source as perception (the app-supplied positions seam), so it works both when players
        // come from SetNet connections AND in headless/seam-driven setups (Mobs without StateSync or even without clients).
        private bool HasInterest(MobInstance mob)
        {
            var (radius, _, _) = PerceptionParamsFor(_brains.TryGetValue(mob.Type, out var b) ? b : null!);
            var r2 = radius * radius;
            foreach (var key in _positions.AllPlayers())
            {
                var pos = _positions.PositionOf(key);
                if (pos != null && Vec3.DistanceSquared(mob.Position, pos.Value) <= r2) return true;
            }
            return false;
        }

        // ---- server-push (interest-filtered) ----

        private Task PublishSpawnAsync(MobInstance mob)
            => PublishToInterestedAsync((ushort)MobsEvt.MobSpawned, MobsWire.EncodeSpawn(mob), mob);

        private async Task PublishToInterestedAsync(ushort evt, byte[] body, MobInstance? mob)
        {
            foreach (var peer in _online.Values)
            {
                if (mob != null && !PeerInterestedIn(peer, mob)) continue;
                try { await peer.PublishRawAsync(Channels.Mobs, evt, body).ConfigureAwait(false); }
                catch { /* peer dropping; skip */ }
            }
        }

        // A peer is interested in a mob if the peer's player is within the mob's aggro radius (spawn/despawn filtering).
        private bool PeerInterestedIn(BasePeer peer, MobInstance mob)
        {
            var key = _options.PlayerKey(peer);
            var pos = _positions.PositionOf(key);
            if (pos == null) return false;
            var (radius, _, _) = PerceptionParamsFor(_brains.TryGetValue(mob.Type, out var b) ? b : null!);
            return Vec3.DistanceSquared(mob.Position, pos.Value) <= radius * radius;
        }

        // Aggro is a discrete cue (not spatially filtered here — clients ignore unknown mob ids).
        internal Task PublishAggroAsync(MobInstance mob, string targetKey)
            => PublishToInterestedAsync((ushort)MobsEvt.MobAggro, MobsWire.EncodeAggro(mob.Id, targetKey), mob);

        private void RaiseMoved(MobInstance mob) { try { MobMoved?.Invoke(mob); } catch { /* isolation */ } }
        private void RaiseUpdated(MobInstance mob) { try { MobUpdated?.Invoke(mob); } catch { /* isolation */ } }

        // ---- helpers ----

        private static bool IsSelf(MobInstance mob, string targetKey)
            => string.IsNullOrEmpty(targetKey) || targetKey == mob.Id;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long NowMs() => Clock.ElapsedMilliseconds;

        private static IPlayerPositions ResolvePositions(MobOptions o)
        {
            if (o.Players != null) return o.Players;
            return new DelegatePlayerPositions(o.PlayerPosition ?? (_ => null), o.AllPlayers ?? Array.Empty<string>);
        }

        private static IPathfinder? TryBuildPathfinder(IGeoData? geo)
        {
            if (geo == null) return null;
            try { return Pathfinding.For(geo); } catch { return null; }   // unsupported geo kind → straight-line
        }

        private static IDamageSink ResolveDamageSink(IServiceProvider services)
            => services.GetService(typeof(IDamageSink)) as IDamageSink ?? new BuiltInPlayerHp();

        // ---- lifecycle / registry ----

        internal static MobServer Enable(BaseServer server, MobOptions options)
            => Servers.GetOrAdd(server, s => new MobServer(s, options ?? new MobOptions()));

        internal static MobServer? For(BaseServer? server)
            => server != null && Servers.TryGetValue(server, out var s) ? s : null;

        /// <inheritdoc/>
        public void Dispose() { _tickReg?.Dispose(); _timer?.Dispose(); _timer = null; }

        // ---- MobContext implementation ----

        /// <summary>The concrete <see cref="MobContext"/> handed to a brain — records intents against a mob's runtime.</summary>
        private sealed class Ctx : MobContext
        {
            private readonly MobServer _server;
            private readonly MobRuntime _runtime;

            public Ctx(MobServer server, MobRuntime runtime) { _server = server; _runtime = runtime; }

            public MobInstance Mob => _runtime.Mob;
            public IServiceProvider Services => _server._services;

            public void MoveTo(Vec3 pos) => _runtime.MoveGoal = pos;

            public void Face(Vec3 pos)
            {
                var to = pos - _runtime.Mob.Position;
                if (to.LengthSquared > 1e-6f) _runtime.Facing = to.Normalized;
            }

            public void SetTarget(string? playerKey)
            {
                var mob = _runtime.Mob;
                var prev = mob.Target;
                mob.Target = playerKey;
                if (playerKey == null) { _runtime.MoveGoal = null; return; }
                if (prev != playerKey) _ = _server.PublishAggroAsync(mob, playerKey);   // aggro cue on (re)target
            }

            public Task UseAbilityAsync(string abilityId, string targetKey)
                => _server.StartAbilityAsync(_runtime, abilityId, targetKey);

            public void Say(string emote) { /* flavour broadcast is app-owned; no wire event in v1 */ }
        }
    }

    /// <summary>Optional loot/xp hook resolved from <c>MobOptions.Services</c> — the app grants drops/XP on a kill.</summary>
    public interface IMobLootSink
    {
        /// <summary>Called when a mob dies. Roll the loot table / award XP to the killer or threat-topper.</summary>
        void OnMobKilled(MobInstance mob, string? killerKey);
    }

    /// <summary>Per-mob server-only runtime: the instance, its brain, movement/cast bookkeeping, and cooldown tracking.</summary>
    internal sealed class MobRuntime
    {
        public readonly MobInstance Mob;
        public readonly IMobBrain Brain;
        public readonly int RespawnMs;

        public Vec3? MoveGoal;             // the current MoveTo intent
        public Vec3 Facing;                // last Face direction
        public PathFollower? Follower;     // active path follower (pathfinder mode)
        public Vec3 PathGoal;              // the goal the current path was computed toward
        public bool MoverActive;           // a delegated IMobMover currently has a goal for this mob

        private readonly Dictionary<string, long> _cooldownUntil = new Dictionary<string, long>();          // mob ability cooldowns
        private readonly Dictionary<string, long> _playerAttackAt = new Dictionary<string, long>();          // player→mob attack pacing

        public MobRuntime(MobInstance mob, IMobBrain brain, int respawnMs) { Mob = mob; Brain = brain; RespawnMs = respawnMs; }

        public bool IsOffCooldown(string abilityId, long nowMs)
            => !_cooldownUntil.TryGetValue(abilityId, out var until) || nowMs >= until;

        public void SetOnCooldown(string abilityId, long untilMs) => _cooldownUntil[abilityId] = untilMs;

        public bool IsPlayerAttackReady(string playerKey, string abilityId, long nowMs, int cooldownMs)
        {
            var k = playerKey + "|" + abilityId;
            return !_playerAttackAt.TryGetValue(k, out var at) || nowMs - at >= cooldownMs;
        }

        public void MarkPlayerAttack(string playerKey, string abilityId, long nowMs)
            => _playerAttackAt[playerKey + "|" + abilityId] = nowMs;
    }

    /// <summary>An empty service provider used when the app supplies none.</summary>
    internal sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new EmptyServices();
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Attaches the mob hub to a server by composition.</summary>
    public static class MobServerExtensions
    {
        /// <summary>Enables the server-side mob hub; returns it so game logic can register brains and spawn mobs.</summary>
        public static MobServer UseMobs(this BaseServer server, MobOptions? options = null)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return MobServer.Enable(server, options ?? new MobOptions());
        }
    }

    /// <summary>
    /// Auto-discovered channel service for mob commands (currently: a player attacking a mob). Validates range/cooldown
    /// server-side and replies accepted/rejected. Rides the unified protocol on <see cref="Channels.Mobs"/>.
    /// </summary>
    [ProtocolChannel(Channels.Mobs)]
    public sealed class MobsChannelService : IChannelService
    {
        /// <inheritdoc/>
        public async Task HandleAsync(ChannelRequest request)
        {
            var hub = MobServer.For(request.Peer.CurrentPeerInfo.Server);
            if (hub == null) throw new ProtocolException("mobs are not configured on this server");

            switch ((MobsOp)request.Op)
            {
                case MobsOp.Attack:
                {
                    var (mobId, abilityId) = MobsWire.DecodeAttack(request.RawBody);
                    var (ok, reason) = await hub.PlayerAttackAsync(request.Peer, mobId, abilityId).ConfigureAwait(false);
                    await request.ReplyRawAsync(MobsWire.EncodeAttackReply(ok, reason)).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new ProtocolException($"unknown mobs op {request.Op}");
            }
        }
    }

    /// <summary>One-time bootstrap so the mobs channel service is discovered. Call at startup on both ends.</summary>
    public static class MobsRuntime
    {
        /// <summary>Ensures the mobs layer is discoverable.</summary>
        public static void Enable() { _ = typeof(MobsChannelService); }
    }
}
