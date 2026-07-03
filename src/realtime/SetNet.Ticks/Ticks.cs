using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SetNet.Ticks
{
    /// <summary>
    /// A central update loop. Register things into named <see cref="TickChannel"/>s — each with its own rate (Hz) and
    /// priority — and the scheduler drives them all from one place instead of every system running its own timer or
    /// being ticked by hand. Channels advance on a per-channel <b>fixed timestep</b> (deterministic, engine-independent).
    ///
    /// <para>Drive it either way:</para>
    /// <list type="bullet">
    ///   <item><see cref="Start"/> — an internal <see cref="Timer"/> pumps everything (typical dedicated server).</item>
    ///   <item><see cref="Pump"/> — call it yourself from your own loop (Unity <c>Update</c>/<c>FixedUpdate</c>, a custom game loop).</item>
    /// </list>
    ///
    /// <example>
    /// <code>
    /// var ticks = new TickScheduler();
    /// var movement = ticks.Channel("movement", hz: 30, priority: 10);
    /// var ai       = ticks.Channel("ai",       hz: 10, priority: 5);
    /// var slow     = ticks.Channel("slow",     hz: 1);
    ///
    /// movement.Add(loco);                            // LocomotionSystem : ITickable
    /// ai.Add(mobs);                                  // MobServer : IAsyncTickable
    /// ai.Add(tree.Bind(ctx));                        // BehaviorTree via Bind(context)
    /// slow.Add(t => RegenTick(t.DeltaSeconds));      // any lambda
    ///
    /// ticks.Start();                                 // one place drives all of it
    /// </code>
    /// </example>
    /// </summary>
    public sealed class TickScheduler : IDisposable, ITickHost
    {
        private readonly List<TickChannel> _channels = new List<TickChannel>();
        private readonly object _gate = new object();
        private Timer? _timer;
        private long _lastStamp;
        private long _frame;
        private int _pumping;
        private bool _disposed;

        /// <summary>When true, <see cref="Pump"/> advances the frame counter but ticks nothing. Channels keep their accumulators.</summary>
        public bool Paused { get; set; }

        /// <summary>The scheduler's current frame number (incremented once per <see cref="Pump"/>).</summary>
        public long Frame => Interlocked.Read(ref _frame);

        /// <summary>All channels created so far, highest priority first (the order they tick in).</summary>
        public IReadOnlyList<TickChannel> Channels
        {
            get { lock (_gate) return _channels.OrderByDescending(c => c.Priority).ToList(); }
        }

        /// <summary>
        /// Gets (or creates on first use) a channel by name. The <paramref name="hz"/> and <paramref name="priority"/>
        /// are applied only when the channel is first created; fetch an existing channel and set its properties to change them.
        /// </summary>
        /// <param name="name">Channel identity (e.g. "movement", "ai", "slow").</param>
        /// <param name="hz">Ticks per second. Each registered item is invoked with a fixed <c>1000/hz</c> ms step.</param>
        /// <param name="priority">Higher priority channels tick earlier within a single pump.</param>
        public TickChannel Channel(string name, int hz = 10, int priority = 0)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Channel name is required.", nameof(name));
            lock (_gate)
            {
                var existing = _channels.FirstOrDefault(c => c.Name == name);
                if (existing != null) return existing;
                var ch = new TickChannel(name, hz, priority);
                _channels.Add(ch);
                return ch;
            }
        }

        /// <summary>
        /// Starts the internal driver: a timer fires at <paramref name="baseHz"/> and pumps the scheduler with the real
        /// elapsed time, so each channel advances at its own rate. Set <paramref name="baseHz"/> at or above your fastest
        /// channel's Hz (default 60). Calling this when already running restarts the timer.
        /// </summary>
        /// <param name="baseHz">Driver frequency; set at or above your fastest channel (default 60).</param>
        public void Start(int baseHz = 60)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TickScheduler));
            if (baseHz < 1) baseHz = 1;
            Stop();
            _lastStamp = Stopwatch.GetTimestamp();
            var periodMs = Math.Max(1, (int)Math.Round(1000.0 / baseHz));
            _timer = new Timer(_ =>
            {
                var now = Stopwatch.GetTimestamp();
                var dtMs = (now - Interlocked.Read(ref _lastStamp)) * 1000.0 / Stopwatch.Frequency;
                Interlocked.Exchange(ref _lastStamp, now);
                Pump(dtMs);
            }, null, periodMs, periodMs);
        }

        /// <summary>Stops the internal timer driver (if any). Channels and registrations are preserved.</summary>
        public void Stop()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            t?.Dispose();
        }

        /// <summary>
        /// Advances every channel by <paramref name="realDtMs"/> of wall-clock time. Each channel fires its items zero or
        /// more times (its accumulator crossing the fixed step), in priority order. Re-entrant calls are ignored, so it is
        /// safe to call from a timer. Call this yourself when driving the scheduler from an external loop.
        /// </summary>
        public void Pump(double realDtMs)
        {
            if (Interlocked.Exchange(ref _pumping, 1) != 0) return; // never overlap a pump
            try
            {
                var frame = Interlocked.Increment(ref _frame);
                if (Paused || realDtMs <= 0) return;

                TickChannel[] snapshot;
                lock (_gate) snapshot = _channels.OrderByDescending(c => c.Priority).ToArray();
                foreach (var ch in snapshot)
                    ch.Advance(realDtMs, frame);
            }
            finally
            {
                Interlocked.Exchange(ref _pumping, 0);
            }
        }

        /// <summary>
        /// Makes this scheduler the ambient <see cref="TickHost.Current"/>, so the library's game-loop systems
        /// (Mobs/Locomotion/Spawning) auto-subscribe to it when created — you never register each one by hand. Returns
        /// this for chaining: <c>var ticks = new TickScheduler().MakeCurrent(); ticks.Start();</c>. Call this before your
        /// <c>UseXxx(...)</c> calls (they check the current host at construction).
        /// </summary>
        public TickScheduler MakeCurrent()
        {
            TickHost.Current = this;
            return this;
        }

        /// <inheritdoc/>
        public IDisposable Register(ITickable tickable, string channel, int hz, int priority = 0)
            => Channel(channel, hz, priority).Add(tickable);

        /// <inheritdoc/>
        public IDisposable Register(IAsyncTickable tickable, string channel, int hz, int priority = 0)
            => Channel(channel, hz, priority).Add(tickable);

        /// <summary>Stops the driver and clears all channels. Also clears <see cref="TickHost.Current"/> if it was this scheduler.</summary>
        public void Dispose()
        {
            _disposed = true;
            Stop();
            lock (_gate) _channels.Clear();
            if (ReferenceEquals(TickHost.Current, this)) TickHost.Current = null;
        }
    }

    /// <summary>Optional per-channel behaviour when it falls behind. See <see cref="TickChannel.MaxSubstepsPerPump"/>.</summary>
    public sealed class TickChannel
    {
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private double _accumulatorMs;

        internal TickChannel(string name, int hz, int priority)
        {
            Name = name;
            Hz = hz;
            Priority = priority;
        }

        /// <summary>Channel identity.</summary>
        public string Name { get; }

        private int _hz;
        /// <summary>Ticks per second. Items are invoked with a fixed <c>1000/Hz</c> ms step. Changeable at runtime.</summary>
        public int Hz
        {
            get => _hz;
            set => _hz = Math.Max(1, value);
        }

        /// <summary>Higher priority channels tick earlier within a single scheduler pump. Changeable at runtime.</summary>
        public int Priority { get; set; }

        /// <summary>When false the channel is skipped entirely (its accumulator freezes). Toggle to pause one channel.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Upper bound on how many fixed steps a single pump may run for this channel, so a long stall can't trigger an
        /// unbounded catch-up burst ("spiral of death"). When the cap is hit, the leftover backlog is dropped. Default 5.
        /// </summary>
        public int MaxSubstepsPerPump { get; set; } = 5;

        /// <summary>Number of registered items currently in this channel.</summary>
        public int Count { get { lock (_gate) return _entries.Count; } }

        /// <summary>Registers a synchronous tickable. Dispose the returned handle to unregister it.</summary>
        public IDisposable Add(ITickable tickable)
        {
            if (tickable == null) throw new ArgumentNullException(nameof(tickable));
            return Register(new Entry(info => tickable.Tick(in info)));
        }

        /// <summary>
        /// Registers an asynchronous tickable. It is invoked fire-and-forget per step (not awaited), so an implementation
        /// that can overrun a step should guard against overlapping runs — the library's <c>MobServer</c> already does.
        /// </summary>
        public IDisposable Add(IAsyncTickable tickable)
        {
            if (tickable == null) throw new ArgumentNullException(nameof(tickable));
            return Register(new Entry(info => { _ = tickable.TickAsync(info); }));
        }

        /// <summary>Registers a synchronous callback. Dispose the returned handle to unregister it.</summary>
        public IDisposable Add(Action<TickInfo> fn)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            return Register(new Entry(info => fn(info)));
        }

        /// <summary>Registers a rate-only callback that ignores the delta.</summary>
        public IDisposable Add(Action fn)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            return Register(new Entry(_ => fn()));
        }

        /// <summary>
        /// Registers an asynchronous callback. It is invoked fire-and-forget per step (not awaited); see the
        /// <see cref="Add(IAsyncTickable)"/> note about guarding overruns.
        /// </summary>
        public IDisposable Add(Func<TickInfo, Task> fn)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            return Register(new Entry(info => { _ = fn(info); }));
        }

        private IDisposable Register(Entry entry)
        {
            lock (_gate) _entries.Add(entry);
            return new Registration(this, entry);
        }

        private void Unregister(Entry entry)
        {
            lock (_gate) _entries.Remove(entry);
        }

        internal void Advance(double realDtMs, long frame)
        {
            if (!Enabled) return;
            var step = 1000.0 / Hz;

            _accumulatorMs += realDtMs;
            var substeps = 0;
            while (_accumulatorMs >= step && substeps < MaxSubstepsPerPump)
            {
                _accumulatorMs -= step;
                substeps++;

                var info = new TickInfo(step, frame);
                Entry[] items;
                lock (_gate) items = _entries.ToArray();
                foreach (var e in items)
                {
                    try { e.Invoke(info); }
                    catch { /* one bad tickable must not stall the whole loop */ }
                }
            }

            // Fell too far behind — drop the backlog rather than spiral.
            if (substeps >= MaxSubstepsPerPump && _accumulatorMs > step)
                _accumulatorMs = 0;
        }

        private sealed class Entry
        {
            private readonly Action<TickInfo> _invoke;
            public Entry(Action<TickInfo> invoke) => _invoke = invoke;
            public void Invoke(in TickInfo info) => _invoke(info);
        }

        private sealed class Registration : IDisposable
        {
            private TickChannel? _channel;
            private readonly Entry _entry;
            public Registration(TickChannel channel, Entry entry) { _channel = channel; _entry = entry; }
            public void Dispose()
            {
                var ch = Interlocked.Exchange(ref _channel, null);
                ch?.Unregister(_entry);
            }
        }
    }
}
