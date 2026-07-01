<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Congestion

**AIMD congestion controller for [SetNet](https://www.nuget.org/packages/SetNet).**

Maintains a target **send rate (bytes/sec)** using additive-increase / multiplicative-decrease — the same family TCP uses. Feed it your delivery/loss signals; ask it for a per-tick **byte budget**; hand that to a [`SetNet.Priority`](https://www.nuget.org/packages/SetNet.Priority) sender so the connection sheds low-priority traffic under congestion instead of bloating queues.

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Congestion
```

```csharp
var cc = new CongestionController(startRate: 128_000);   // 128 KB/s

// from your ack/loss tracking (reliable-UDP acks, app acks, snapshot gaps):
cc.OnDelivered();   // nudges the rate up
cc.OnLoss();        // cuts it back (×0.7)

// each tick, budget a priority flush by the current rate:
await prioritySender.FlushAsync(cc.BudgetForInterval(seconds: 1.0 / tickRate));
```

Rate is clamped to `[minRate, maxRate]`. It's signal-driven and transport-agnostic — you decide what counts as "delivered" and "lost".

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
