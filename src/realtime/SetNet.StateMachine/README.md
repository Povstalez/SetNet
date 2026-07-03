# SetNet.StateMachine

**A tiny generic finite-state machine for [SetNet](https://www.nuget.org/packages/SetNet) AI and gameplay.**

Register states with enter/update/exit callbacks over your own context type, wire up guarded transitions, `Start` in one
state, and `Update(context, dt)` each tick — the first transition whose guard is true fires, otherwise the current
state's update runs. Build one fluently with `StateMachine<TContext>.Build()`. Engine-agnostic, allocation-light, and
free of any wire protocol — wrap one in an `IMobBrain` for mob AI, or use it anywhere.

```csharp
// ctx is whatever your AI needs — position, target, health...
var fsm = StateMachine<Mob>.Build()
    .State("idle",
        onUpdate: (m, dt) => m.LookAround(),
        onEnter:  m => m.PlayAnim("idle"))
    .State("chase",
        onEnter:  m => m.PlayAnim("run"),
        onUpdate: (m, dt) => m.MoveToward(m.Target))
    .Transition("idle",  "chase", m => m.Target != null)
    .Transition("chase", "idle",  m => m.Target == null)
    .Create();                       // or .Start(mob) to build + enter the initial state

fsm.Start("idle", mob);

// each server tick:
fsm.Update(mob, dtMs);               // evaluates guards, then runs the current state
```

## Concepts

- **States** — `State(name, onEnter?, onUpdate?, onExit?)`. `onUpdate` gets `(context, dtMs)`; the first state added is
  the default initial (override with `Initial(name)`).
- **Transitions** — `Transition(from, to, guard)` is checked only in state `from`; `AnyTransition(to, guard)` is checked
  from **any** state first (good for "flee when low health" style overrides). The first matching guard wins each tick.
- **Driving it** — `Start(state, ctx)` enters the initial state (fires its `OnEnter`); `Update(ctx, dtMs)` ticks it;
  `GoTo(state, ctx)` forces a transition now, bypassing guards. Read `Current` / `CurrentName`, and subscribe to the
  `StateChanged` `(from, to)` event.

## Notes

- **Pure library, no wire protocol.** The FSM runs wherever you tick it — typically server-side inside an `IMobBrain`,
  but it has no networking dependency beyond referencing `SetNet`.
- Guards and callbacks are your delegates over `TContext`, so the machine holds no per-entity state itself — one built
  machine can drive many entities, each with its own context instance.

## License

MIT
