<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Lockstep

**Deterministic lockstep engine for [SetNet](https://www.nuget.org/packages/SetNet).**

Two ways to network a game: **replicate state** (server streams positions — see [`SetNet.StateSync`](https://www.nuget.org/packages/SetNet.StateSync)), or **replicate inputs** (every client runs the *same* simulation from the *same* inputs). The second is **lockstep**, and it's how most RTS games work: sending "player 2 ordered these 40 units to move" is tiny compared to streaming 40 unit positions every tick.

Here, the server collects each participant's input for a turn and — once **all** inputs are in (or the turn times out) — broadcasts the complete input set. Every client then advances its simulation by one turn, identically.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Lockstep
```

## Setup

```csharp
LockstepRuntime.Enable();     // once at startup, both ends, before creating client/server
```

## Server

```csharp
server.UseLockstep(new LockstepOptions { TurnTimeoutMs = 200 });
```

The server auto-enrolls every connected peer as a participant, runs the turn clock, and relays inputs **opaquely** (it never deserializes them, so it stays serializer-agnostic).

## Client (typed)

```csharp
// choose your per-turn input type — (de)serialized via the registered serializer:
var ls = client.UseLockstep<PlayerCommand>();

ls.TurnReady += (turn, inputs) =>       // inputs: playerId -> PlayerCommand
{
    foreach (var (playerId, cmd) in inputs)
        Simulation.Apply(playerId, cmd);
    Simulation.Step();                  // advance exactly one deterministic tick
};

// each turn, submit your command:
ls.SubmitInput(new PlayerCommand { Move = dir, Fire = firing });
```

`TurnReady` hands you a `IReadOnlyDictionary<string, PlayerCommand>` — **fully typed**, no raw bytes. Use `client.UseLockstep<byte[]>()` if you want to manage serialization yourself.

## Determinism is on you

Lockstep only guarantees every client receives the **same inputs in the same order**. Whether they compute the **same result** depends on your simulation:

- Prefer **fixed-point** or integer math; be careful with `float` (order-of-operations and platform differences can diverge).
- Don't branch on non-replicated state (wall-clock time, `Random` without a shared seed, iteration order of unordered collections).
- Step the simulation **only** in `TurnReady`, never in a render/`Update` loop.

A divergence (desync) means one client computed a different world — usually fatal for lockstep, so test with a periodic state checksum.

## Options

| Option | Default | Meaning |
|---|---|---|
| `TurnTimeoutMs` | `200` | Max wait for every participant's input before finalizing a turn anyway (drops laggards) |

## How it works

- The server holds a turn number `T`. Clients submit an input tagged for their current turn.
- A turn finalizes when **all participants** have submitted for it (fast path) **or** after `TurnTimeoutMs` (safety net). The finalized input set is broadcast; `T` advances.
- Clients run a turn or two behind real time (the input delay), which is the standard lockstep trade-off for perfect consistency.
- Reserved wire types `65514 / 65515`.

## Notes

- Best for turn-based and RTS-style games; for fast action shooters prefer state replication + [prediction](https://www.nuget.org/packages/SetNet.StateSync.Prediction).
- A dropped/slow player is dropped from a turn after the timeout — handle "missing input" gracefully (e.g. repeat last command).

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
