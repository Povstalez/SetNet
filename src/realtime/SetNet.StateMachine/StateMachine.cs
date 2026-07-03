using System;
using System.Collections.Generic;

namespace SetNet.StateMachine
{
    /// <summary>One state: optional enter/update/exit callbacks over your context type.</summary>
    public sealed class State<TContext>
    {
        /// <summary>The state's unique name.</summary>
        public string Name { get; }
        /// <summary>Called once when the machine enters this state.</summary>
        public Action<TContext>? OnEnter { get; }
        /// <summary>Called every <see cref="StateMachine{TContext}.Update"/> while this is the current state (ctx, dtMs).</summary>
        public Action<TContext, float>? OnUpdate { get; }
        /// <summary>Called once when the machine leaves this state.</summary>
        public Action<TContext>? OnExit { get; }

        /// <summary>Creates a state.</summary>
        public State(string name, Action<TContext>? onEnter = null, Action<TContext, float>? onUpdate = null, Action<TContext>? onExit = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            OnEnter = onEnter; OnUpdate = onUpdate; OnExit = onExit;
        }
    }

    /// <summary>
    /// A minimal generic finite-state machine. Register states and guarded transitions, <see cref="Start"/> in one, and
    /// call <see cref="Update"/> each tick — the first transition whose guard is true fires (any-state transitions are
    /// checked first), otherwise the current state's update runs. Build one fluently with <see cref="Build"/>.
    /// </summary>
    public sealed class StateMachine<TContext>
    {
        private readonly Dictionary<string, State<TContext>> _states = new Dictionary<string, State<TContext>>();
        private readonly Dictionary<string, List<(string to, Func<TContext, bool> guard)>> _transitions = new Dictionary<string, List<(string, Func<TContext, bool>)>>();
        private readonly List<(string to, Func<TContext, bool> guard)> _anyTransitions = new List<(string, Func<TContext, bool>)>();

        /// <summary>The current state (null before <see cref="Start"/>).</summary>
        public State<TContext>? Current { get; private set; }
        /// <summary>The current state's name (null before <see cref="Start"/>).</summary>
        public string? CurrentName => Current?.Name;

        /// <summary>Raised on every state change (from, to).</summary>
        public event Action<string?, string>? StateChanged;

        /// <summary>Registers a state.</summary>
        public StateMachine<TContext> AddState(State<TContext> state)
        {
            _states[state.Name] = state;
            return this;
        }

        /// <summary>Registers a state from callbacks.</summary>
        public StateMachine<TContext> AddState(string name, Action<TContext>? onEnter = null, Action<TContext, float>? onUpdate = null, Action<TContext>? onExit = null)
            => AddState(new State<TContext>(name, onEnter, onUpdate, onExit));

        /// <summary>Adds a guarded transition from one state to another.</summary>
        public StateMachine<TContext> AddTransition(string from, string to, Func<TContext, bool> guard)
        {
            if (!_transitions.TryGetValue(from, out var list)) _transitions[from] = list = new List<(string, Func<TContext, bool>)>();
            list.Add((to, guard));
            return this;
        }

        /// <summary>Adds a transition allowed from ANY state (checked before per-state ones).</summary>
        public StateMachine<TContext> AddAnyTransition(string to, Func<TContext, bool> guard)
        {
            _anyTransitions.Add((to, guard));
            return this;
        }

        /// <summary>Enters the initial state (fires its OnEnter).</summary>
        public void Start(string state, TContext context)
        {
            if (!_states.TryGetValue(state, out var s)) throw new ArgumentException($"Unknown state '{state}'.", nameof(state));
            Current = s;
            s.OnEnter?.Invoke(context);
            StateChanged?.Invoke(null, s.Name);
        }

        /// <summary>Forces a transition to <paramref name="state"/> now (exit current, enter new), bypassing guards.</summary>
        public void GoTo(string state, TContext context)
        {
            if (!_states.TryGetValue(state, out var s)) throw new ArgumentException($"Unknown state '{state}'.", nameof(state));
            var from = Current;
            if (ReferenceEquals(from, s)) return;
            from?.OnExit?.Invoke(context);
            Current = s;
            s.OnEnter?.Invoke(context);
            StateChanged?.Invoke(from?.Name, s.Name);
        }

        /// <summary>Ticks the machine: evaluates transitions (any-state first), else runs the current state's update.</summary>
        public void Update(TContext context, float dtMs)
        {
            if (Current == null) return;

            foreach (var (to, guard) in _anyTransitions)
                if (Current.Name != to && guard(context)) { GoTo(to, context); return; }

            if (_transitions.TryGetValue(Current.Name, out var list))
                foreach (var (to, guard) in list)
                    if (guard(context)) { GoTo(to, context); return; }

            Current.OnUpdate?.Invoke(context, dtMs);
        }

        /// <summary>
        /// Binds this machine to a fixed <paramref name="context"/> so it can be driven by a <c>SetNet.Ticks.TickScheduler</c>:
        /// <c>channel.Add(fsm.Bind(context))</c>. The returned tickable calls <see cref="Update"/> with the channel's fixed
        /// step each time.
        /// </summary>
        public SetNet.Ticks.ITickable Bind(TContext context) => new BoundTicker(this, context);

        private sealed class BoundTicker : SetNet.Ticks.ITickable
        {
            private readonly StateMachine<TContext> _fsm;
            private readonly TContext _ctx;
            public BoundTicker(StateMachine<TContext> fsm, TContext ctx) { _fsm = fsm; _ctx = ctx; }
            public void Tick(in SetNet.Ticks.TickInfo tick) => _fsm.Update(_ctx, (float)tick.DeltaMs);
        }

        /// <summary>Starts a fluent builder.</summary>
        public static Builder Build() => new Builder();

        /// <summary>Fluent builder for a <see cref="StateMachine{TContext}"/>.</summary>
        public sealed class Builder
        {
            private readonly StateMachine<TContext> _fsm = new StateMachine<TContext>();
            private string? _initial;

            /// <summary>Adds a state.</summary>
            public Builder State(string name, Action<TContext>? onEnter = null, Action<TContext, float>? onUpdate = null, Action<TContext>? onExit = null)
            { _fsm.AddState(name, onEnter, onUpdate, onExit); _initial ??= name; return this; }

            /// <summary>Adds a guarded transition.</summary>
            public Builder Transition(string from, string to, Func<TContext, bool> guard) { _fsm.AddTransition(from, to, guard); return this; }

            /// <summary>Adds an any-state transition.</summary>
            public Builder AnyTransition(string to, Func<TContext, bool> guard) { _fsm.AddAnyTransition(to, guard); return this; }

            /// <summary>Sets the initial state (defaults to the first added).</summary>
            public Builder Initial(string state) { _initial = state; return this; }

            /// <summary>Returns the built machine (call <see cref="StateMachine{TContext}.Start"/> to enter the initial state).</summary>
            public StateMachine<TContext> Create() => _fsm;

            /// <summary>Builds and immediately starts the machine in its initial state.</summary>
            public StateMachine<TContext> Start(TContext context)
            {
                if (_initial == null) throw new InvalidOperationException("No states were added.");
                _fsm.Start(_initial, context);
                return _fsm;
            }
        }
    }
}
