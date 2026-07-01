# SetNet.Lockstep

**Deterministic lockstep engine for [SetNet](https://www.nuget.org/packages/SetNet).**

Instead of streaming state, the server collects each participant's **input** for a turn and — once **all** inputs are in (or the turn times out) — broadcasts the complete input set so every client advances its simulation identically. Ideal for RTS and other deterministic games where sending inputs is far cheaper than sending state.

```csharp
LockstepRuntime.Enable();   // startup, both ends

// server (auto-enrolls connected peers as participants):
server.UseLockstep(new LockstepOptions { TurnTimeoutMs = 200 });

// client:
var ls = client.UseLockstep();
ls.TurnReady += (turn, inputs) => Simulate(turn, inputs);   // inputs: playerId -> bytes
ls.SubmitInput(Serialize(myInput));
```

**Determinism of the simulation itself is your responsibility** — use fixed-point or carefully-ordered float math so every client computes the same result from the same inputs.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
