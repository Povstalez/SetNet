<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.StateSync.Prediction

**Client-side prediction & reconciliation for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

On a server-authoritative game the client can't wait for the server to confirm every move — that round-trip would make the local player feel laggy. The fix is **prediction**: apply the input locally *right now*, but keep it around until the server acknowledges it. When the authoritative snapshot arrives, **snap** the owned entity to the server's state and **replay** the inputs the server hasn't processed yet on top of it (rewind & replay). The player stays responsive and the world stays server-authoritative.

`PredictionBuffer<TInput>` is the bookkeeping for that loop: record each input against the sequence number `ClientReplication.SendInput` stamps on it, then `Reconcile` against the sequence the server echoes back (`ClientReplication.LastProcessedInput`).

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.StateSync
dotnet add package SetNet.StateSync.Prediction
```

## Usage

```csharp
using SetNet.StateSync;
using SetNet.StateSync.Prediction;

var replication = client.UseStateSync(new StateSyncOptions { InterpolationDelayMs = 100 });
var buffer = new PredictionBuffer<MyInput>();

// --- each input frame: predict locally, then send + record ---
MyInput input = ReadInput();
Apply(input);                                       // move the owned entity NOW (prediction)
uint seq = replication.SendInput(Serialize(input)); // send to server, get the sequence stamped on it
buffer.Record(seq, input);                          // remember it until the server acks

// --- each corrective snapshot: snap to authority, then replay unacked inputs ---
replication.Update();
var owned = replication.OwnedEntity;                // NetworkEntityView? for the entity you own
if (owned != null)
{
    SnapLocalEntityTo(owned.GetVec3(0));            // authoritative position (rewind)
    buffer.Reconcile(replication.LastProcessedInput, Apply);  // drop acked, replay the rest
}
```

`Reconcile` removes every input the server has already processed (`seq ≤ lastProcessedInput`) and re-applies the remaining ones in order through the `apply` callback — the same `Apply` you use for local prediction. After it returns, the owned entity is at the authoritative state advanced by all still-in-flight inputs.

## API

| Member | Purpose |
|---|---|
| `new PredictionBuffer<TInput>(int maxPending = 256)` | Create the buffer; keeps at most `maxPending` unacknowledged inputs |
| `void Record(uint seq, TInput input)` | Record an input you just applied locally, tagged with the seq returned by `SendInput` |
| `void Reconcile(uint lastProcessedInput, Action<TInput> apply)` | Drop inputs with `seq ≤ lastProcessedInput`, replay the rest in order via `apply` |
| `int PendingCount` | Number of inputs awaiting server acknowledgement |

## Notes

- **Determinism is on you.** Reconciliation only feels right if replaying the same input from the same state produces the same result. Keep your movement/simulation step deterministic and side-effect-free (no per-frame randomness, no reads of wall-clock time inside `apply`).
- **Snap before you replay.** `Reconcile` replays on top of *current* local state, so first snap the owned entity to the server's authoritative values (`replication.OwnedEntity`), then call `Reconcile`.
- **Predict only what you own.** Prediction is for the local player's entity (`OwnedEntity`); everything else is smoothed by interpolation in `SetNet.StateSync`. Don't predict entities the server owns.
- **Buffer cap:** if more than `maxPending` inputs pile up unacknowledged (e.g. a long stall), the oldest are dropped. The default 256 covers many seconds at typical input rates.
- Pure bookkeeping — no network I/O of its own. It sits alongside the `ClientReplication` you already drive each frame.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
