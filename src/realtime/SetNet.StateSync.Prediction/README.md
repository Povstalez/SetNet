# SetNet.StateSync.Prediction

**Client-side prediction & reconciliation for [SetNet.StateSync](https://www.nuget.org/packages/SetNet.StateSync).**

Keep the local player responsive while staying server-authoritative. Apply inputs immediately, record them, and when a corrective snapshot arrives, **replay** the still-unacknowledged inputs on top of the server state (rewind & replay).

```csharp
var buffer = new PredictionBuffer<MyInput>();

// each input:
var input = ReadInput();
Apply(input);                                   // predict locally
var seq = replication.SendInput(Serialize(input));
buffer.Record(seq, input);

// each snapshot (server correction):
SnapOwnedEntityTo(replication.OwnedEntity);     // authoritative state
buffer.Reconcile(replication.LastProcessedInput, Apply);   // replay unacked inputs
```

The buffer discards inputs the server has already processed and replays the rest in order.

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
