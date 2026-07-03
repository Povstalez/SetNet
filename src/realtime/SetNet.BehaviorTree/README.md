# SetNet.BehaviorTree

**A small generic behavior tree for [SetNet](https://www.nuget.org/packages/SetNet) AI.**

Assemble a tree from `Sequence` / `Selector` / `Parallel` composites, `Inverter` / `Succeeder` / `Repeat` / `Cooldown`
decorators, and `Do` / `Condition` / `Wait` leaves with a fluent builder, then `Tick(ctx, dtMs)` it each frame over your
own context type. Nodes return `BtStatus.Success` / `Failure` / `Running`, and `Running` leaves are resumed on the next
tick. Engine-agnostic and free of any wire protocol — wrap one in an `IMobBrain` for mob AI, or use it anywhere.

```csharp
// A goblin: attack if in range, else chase the target, else idle.
var tree = BehaviorTree<Mob>.Build()
    .Selector()
        .Sequence()
            .Condition(m => m.HasTarget && m.InAttackRange)
            .Cooldown(1500)                         // don't swing more than once per 1.5s
                .Do(m => m.Attack(m.Target))
            .End()
        .End()
        .Sequence()
            .Condition(m => m.HasTarget)
            .Do((m, dt) => m.MoveToward(m.Target) ? BtStatus.Running : BtStatus.Success)
        .End()
        .Do(m => m.Idle())                          // fallback leaf
    .End()
    .Create();

// each server tick:
var status = tree.Tick(mob, dtMs);
```

## Concepts

- **Composites** open with `Sequence()` / `Selector()` / `Parallel()` and close with `End()`. Sequence is AND (fails on
  the first failure), Selector is OR (succeeds on the first success), Parallel ticks every child each frame.
- **Decorators** wrap exactly one node: `Inverter()` swaps Success/Failure, `Succeeder()` forces Success, `Repeat(count)`
  loops a child (`count ≤ 0` = forever), `Cooldown(ms)` blocks re-running a child until the timer elapses.
- **Leaves**: `Do(Func<TContext, float, BtStatus>)` for a full action, `Do(Action<TContext>)` for a fire-and-succeed side
  effect, `Condition(pred)` for a boolean Success/Failure gate, `Wait(ms)` to stay Running for a duration. `Node(...)`
  attaches an already-built `BtNode<TContext>`.
- **Ticking**: `Tick(ctx, dtMs)` runs one pass and returns a `BtStatus`; `Reset()` clears every node's in-progress state.
  Sequence/Selector remember the child that was `Running` and resume there next tick.

## Notes

- **Pure library, no wire protocol.** The tree runs wherever you tick it — typically server-side inside an `IMobBrain`,
  but it has no networking dependency beyond referencing `SetNet`.
- Actions and conditions are your delegates over `TContext`, so one built tree can drive many entities, each with its own
  context instance.
- Prefer a simpler shape? Reach for [`SetNet.StateMachine`](https://www.nuget.org/packages/SetNet.StateMachine) when a
  handful of states and transitions is enough.

## License

MIT
