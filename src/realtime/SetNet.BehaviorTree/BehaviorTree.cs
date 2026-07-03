using System;
using System.Collections.Generic;

namespace SetNet.BehaviorTree
{
    /// <summary>The result of ticking a node.</summary>
    public enum BtStatus
    {
        /// <summary>The node finished successfully.</summary>
        Success,
        /// <summary>The node finished but failed.</summary>
        Failure,
        /// <summary>The node is still working; tick it again next frame.</summary>
        Running,
    }

    /// <summary>Base class for every behavior-tree node over a context type.</summary>
    public abstract class BtNode<TContext>
    {
        /// <summary>Ticks the node with the shared context and delta time (ms).</summary>
        public abstract BtStatus Tick(TContext ctx, float dtMs);
        /// <summary>Resets any internal progress (called when a parent abandons a running child).</summary>
        public virtual void Reset() { }
    }

    // ---- leaves ----

    /// <summary>A leaf that runs a function each tick and returns its status.</summary>
    public sealed class ActionNode<TContext> : BtNode<TContext>
    {
        private readonly Func<TContext, float, BtStatus> _fn;
        /// <summary>Creates an action leaf.</summary>
        public ActionNode(Func<TContext, float, BtStatus> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs) => _fn(ctx, dtMs);
    }

    /// <summary>A leaf that returns Success/Failure from a boolean predicate.</summary>
    public sealed class ConditionNode<TContext> : BtNode<TContext>
    {
        private readonly Func<TContext, bool> _pred;
        /// <summary>Creates a condition leaf.</summary>
        public ConditionNode(Func<TContext, bool> pred) => _pred = pred ?? throw new ArgumentNullException(nameof(pred));
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs) => _pred(ctx) ? BtStatus.Success : BtStatus.Failure;
    }

    /// <summary>A leaf that stays Running for a duration, then Succeeds.</summary>
    public sealed class WaitNode<TContext> : BtNode<TContext>
    {
        private readonly float _durationMs;
        private float _elapsed;
        /// <summary>Creates a wait leaf.</summary>
        public WaitNode(float durationMs) => _durationMs = durationMs;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            _elapsed += dtMs;
            if (_elapsed >= _durationMs) { _elapsed = 0; return BtStatus.Success; }
            return BtStatus.Running;
        }
        /// <inheritdoc/>
        public override void Reset() => _elapsed = 0;
    }

    // ---- composites ----

    /// <summary>Ticks children in order; fails on the first failure, succeeds only if all succeed (AND).</summary>
    public sealed class Sequence<TContext> : BtNode<TContext>
    {
        private readonly List<BtNode<TContext>> _children;
        private int _running;
        /// <summary>Creates a sequence over its children.</summary>
        public Sequence(List<BtNode<TContext>> children) => _children = children;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            for (var i = _running; i < _children.Count; i++)
            {
                var s = _children[i].Tick(ctx, dtMs);
                if (s == BtStatus.Running) { _running = i; return BtStatus.Running; }
                if (s == BtStatus.Failure) { _running = 0; return BtStatus.Failure; }
            }
            _running = 0;
            return BtStatus.Success;
        }
        /// <inheritdoc/>
        public override void Reset() { _running = 0; foreach (var c in _children) c.Reset(); }
    }

    /// <summary>Ticks children in order; succeeds on the first success, fails only if all fail (OR).</summary>
    public sealed class Selector<TContext> : BtNode<TContext>
    {
        private readonly List<BtNode<TContext>> _children;
        private int _running;
        /// <summary>Creates a selector over its children.</summary>
        public Selector(List<BtNode<TContext>> children) => _children = children;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            for (var i = _running; i < _children.Count; i++)
            {
                var s = _children[i].Tick(ctx, dtMs);
                if (s == BtStatus.Running) { _running = i; return BtStatus.Running; }
                if (s == BtStatus.Success) { _running = 0; return BtStatus.Success; }
            }
            _running = 0;
            return BtStatus.Failure;
        }
        /// <inheritdoc/>
        public override void Reset() { _running = 0; foreach (var c in _children) c.Reset(); }
    }

    /// <summary>Ticks all children each frame; fails if any fails, succeeds when all succeed, else Running.</summary>
    public sealed class Parallel<TContext> : BtNode<TContext>
    {
        private readonly List<BtNode<TContext>> _children;
        /// <summary>Creates a parallel over its children.</summary>
        public Parallel(List<BtNode<TContext>> children) => _children = children;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            var succeeded = 0;
            var anyFail = false;
            foreach (var c in _children)
            {
                var s = c.Tick(ctx, dtMs);
                if (s == BtStatus.Success) succeeded++;
                else if (s == BtStatus.Failure) anyFail = true;
            }
            if (anyFail) return BtStatus.Failure;
            return succeeded == _children.Count ? BtStatus.Success : BtStatus.Running;
        }
        /// <inheritdoc/>
        public override void Reset() { foreach (var c in _children) c.Reset(); }
    }

    // ---- decorators ----

    /// <summary>Swaps Success ↔ Failure of its child (Running passes through).</summary>
    public sealed class Inverter<TContext> : BtNode<TContext>
    {
        private readonly BtNode<TContext> _child;
        /// <summary>Creates an inverter.</summary>
        public Inverter(BtNode<TContext> child) => _child = child;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            var s = _child.Tick(ctx, dtMs);
            return s == BtStatus.Success ? BtStatus.Failure : s == BtStatus.Failure ? BtStatus.Success : BtStatus.Running;
        }
        /// <inheritdoc/>
        public override void Reset() => _child.Reset();
    }

    /// <summary>Always reports Success once the child finishes (turns Failure into Success).</summary>
    public sealed class Succeeder<TContext> : BtNode<TContext>
    {
        private readonly BtNode<TContext> _child;
        /// <summary>Creates a succeeder.</summary>
        public Succeeder(BtNode<TContext> child) => _child = child;
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            var s = _child.Tick(ctx, dtMs);
            return s == BtStatus.Running ? BtStatus.Running : BtStatus.Success;
        }
        /// <inheritdoc/>
        public override void Reset() => _child.Reset();
    }

    /// <summary>Repeats the child N times (or forever if count ≤ 0), Running until done.</summary>
    public sealed class Repeater<TContext> : BtNode<TContext>
    {
        private readonly BtNode<TContext> _child;
        private readonly int _count;
        private int _done;
        /// <summary>Creates a repeater (count ≤ 0 = infinite).</summary>
        public Repeater(BtNode<TContext> child, int count) { _child = child; _count = count; }
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            var s = _child.Tick(ctx, dtMs);
            if (s == BtStatus.Running) return BtStatus.Running;
            _child.Reset();
            if (_count <= 0) return BtStatus.Running;   // infinite
            _done++;
            if (_done >= _count) { _done = 0; return BtStatus.Success; }
            return BtStatus.Running;
        }
        /// <inheritdoc/>
        public override void Reset() { _done = 0; _child.Reset(); }
    }

    /// <summary>Blocks re-running the child until a cooldown elapses (returns Failure while cooling down).</summary>
    public sealed class Cooldown<TContext> : BtNode<TContext>
    {
        private readonly BtNode<TContext> _child;
        private readonly float _cooldownMs;
        private float _remaining;
        /// <summary>Creates a cooldown decorator.</summary>
        public Cooldown(BtNode<TContext> child, float cooldownMs) { _child = child; _cooldownMs = cooldownMs; }
        /// <inheritdoc/>
        public override BtStatus Tick(TContext ctx, float dtMs)
        {
            if (_remaining > 0) { _remaining -= dtMs; return BtStatus.Failure; }
            var s = _child.Tick(ctx, dtMs);
            if (s != BtStatus.Running) _remaining = _cooldownMs;
            return s;
        }
        /// <inheritdoc/>
        public override void Reset() { _remaining = 0; _child.Reset(); }
    }

    /// <summary>A behavior tree: a root node you tick each frame.</summary>
    public sealed class BehaviorTree<TContext>
    {
        private readonly BtNode<TContext> _root;
        /// <summary>Wraps a root node.</summary>
        public BehaviorTree(BtNode<TContext> root) => _root = root ?? throw new ArgumentNullException(nameof(root));
        /// <summary>Ticks the tree once.</summary>
        public BtStatus Tick(TContext ctx, float dtMs) => _root.Tick(ctx, dtMs);
        /// <summary>Resets the whole tree.</summary>
        public void Reset() => _root.Reset();
        /// <summary>Starts a fluent builder.</summary>
        public static BehaviorTreeBuilder<TContext> Build() => new BehaviorTreeBuilder<TContext>();

        /// <summary>
        /// Binds this tree to a fixed <paramref name="ctx"/> so it can be driven by a <c>SetNet.Ticks.TickScheduler</c>:
        /// <c>channel.Add(tree.Bind(context))</c>. The returned tickable calls <see cref="Tick"/> with the channel's
        /// fixed step each time — so every tree updates from one place instead of being ticked by hand.
        /// </summary>
        public SetNet.Ticks.ITickable Bind(TContext ctx) => new BoundTicker(this, ctx);

        private sealed class BoundTicker : SetNet.Ticks.ITickable
        {
            private readonly BehaviorTree<TContext> _tree;
            private readonly TContext _ctx;
            public BoundTicker(BehaviorTree<TContext> tree, TContext ctx) { _tree = tree; _ctx = ctx; }
            public void Tick(in SetNet.Ticks.TickInfo tick) => _tree.Tick(_ctx, (float)tick.DeltaMs);
        }
    }

    /// <summary>
    /// Fluent builder for a <see cref="BehaviorTree{TContext}"/>. Open a composite (<c>Sequence()</c>/<c>Selector()</c>/
    /// <c>Parallel()</c>) or a decorator (<c>Inverter()</c>/<c>Repeat()</c>/…), add leaves (<c>Do</c>/<c>Condition</c>/
    /// <c>Wait</c>), and close each with <c>End()</c>. Call <c>Create()</c> to get the tree.
    /// </summary>
    public sealed class BehaviorTreeBuilder<TContext>
    {
        private abstract class Frame { public abstract void Add(BtNode<TContext> child); public abstract BtNode<TContext> Build(); }

        private sealed class CompositeFrame : Frame
        {
            public readonly List<BtNode<TContext>> Children = new List<BtNode<TContext>>();
            public readonly Func<List<BtNode<TContext>>, BtNode<TContext>> Make;
            public CompositeFrame(Func<List<BtNode<TContext>>, BtNode<TContext>> make) => Make = make;
            public override void Add(BtNode<TContext> child) => Children.Add(child);
            public override BtNode<TContext> Build() => Make(Children);
        }

        private sealed class DecoratorFrame : Frame
        {
            private BtNode<TContext>? _child;
            public readonly Func<BtNode<TContext>, BtNode<TContext>> Make;
            public DecoratorFrame(Func<BtNode<TContext>, BtNode<TContext>> make) => Make = make;
            public override void Add(BtNode<TContext> child) { if (_child != null) throw new InvalidOperationException("A decorator takes exactly one child."); _child = child; }
            public override BtNode<TContext> Build() => Make(_child ?? throw new InvalidOperationException("Decorator has no child."));
        }

        private readonly Stack<Frame> _frames = new Stack<Frame>();
        private BtNode<TContext>? _root;

        private BehaviorTreeBuilder<TContext> Push(Frame f) { _frames.Push(f); return this; }
        private BehaviorTreeBuilder<TContext> AddNode(BtNode<TContext> node)
        {
            if (_frames.Count == 0) _root = node;
            else _frames.Peek().Add(node);
            return this;
        }

        /// <summary>Opens a Sequence composite.</summary>
        public BehaviorTreeBuilder<TContext> Sequence() => Push(new CompositeFrame(c => new Sequence<TContext>(c)));
        /// <summary>Opens a Selector composite.</summary>
        public BehaviorTreeBuilder<TContext> Selector() => Push(new CompositeFrame(c => new Selector<TContext>(c)));
        /// <summary>Opens a Parallel composite.</summary>
        public BehaviorTreeBuilder<TContext> Parallel() => Push(new CompositeFrame(c => new Parallel<TContext>(c)));
        /// <summary>Opens an Inverter decorator (wraps the next single node).</summary>
        public BehaviorTreeBuilder<TContext> Inverter() => Push(new DecoratorFrame(c => new Inverter<TContext>(c)));
        /// <summary>Opens a Succeeder decorator.</summary>
        public BehaviorTreeBuilder<TContext> Succeeder() => Push(new DecoratorFrame(c => new Succeeder<TContext>(c)));
        /// <summary>Opens a Repeater decorator (count ≤ 0 = infinite).</summary>
        public BehaviorTreeBuilder<TContext> Repeat(int count) => Push(new DecoratorFrame(c => new Repeater<TContext>(c, count)));
        /// <summary>Opens a Cooldown decorator.</summary>
        public BehaviorTreeBuilder<TContext> Cooldown(float cooldownMs) => Push(new DecoratorFrame(c => new Cooldown<TContext>(c, cooldownMs)));

        /// <summary>Adds an action leaf.</summary>
        public BehaviorTreeBuilder<TContext> Do(Func<TContext, float, BtStatus> fn) => AddNode(new ActionNode<TContext>(fn));
        /// <summary>Adds an action leaf that always Succeeds after running the side effect.</summary>
        public BehaviorTreeBuilder<TContext> Do(Action<TContext> fn) => AddNode(new ActionNode<TContext>((c, _) => { fn(c); return BtStatus.Success; }));
        /// <summary>Adds a condition leaf.</summary>
        public BehaviorTreeBuilder<TContext> Condition(Func<TContext, bool> pred) => AddNode(new ConditionNode<TContext>(pred));
        /// <summary>Adds a wait leaf.</summary>
        public BehaviorTreeBuilder<TContext> Wait(float durationMs) => AddNode(new WaitNode<TContext>(durationMs));
        /// <summary>Adds an already-built node.</summary>
        public BehaviorTreeBuilder<TContext> Node(BtNode<TContext> node) => AddNode(node);

        /// <summary>Closes the current composite/decorator and attaches it to its parent (or makes it the root).</summary>
        public BehaviorTreeBuilder<TContext> End()
        {
            if (_frames.Count == 0) throw new InvalidOperationException("End() without a matching Sequence/Selector/decorator.");
            var frame = _frames.Pop();
            return AddNode(frame.Build());
        }

        /// <summary>Builds the tree (all composites/decorators must be closed).</summary>
        public BehaviorTree<TContext> Create()
        {
            if (_frames.Count != 0) throw new InvalidOperationException("Unclosed composite/decorator — missing End().");
            if (_root == null) throw new InvalidOperationException("The tree is empty.");
            return new BehaviorTree<TContext>(_root);
        }
    }
}
