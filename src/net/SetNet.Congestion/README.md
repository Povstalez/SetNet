<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Congestion

**AIMD congestion controller for [SetNet](https://www.nuget.org/packages/SetNet).**

A `CongestionController` maintains a target **send rate in bytes per second** using additive-increase / multiplicative-decrease — the same control family TCP uses. You feed it your own delivery and loss signals; each acknowledged round nudges the rate up a little, each detected loss cuts it back sharply. Ask it for a per-tick **byte budget** and hand that to a [`SetNet.Priority`](https://www.nuget.org/packages/SetNet.Priority) sender so the connection sheds low-priority traffic under congestion instead of bloating queues.

It is **signal-driven and transport-agnostic** — the controller never touches a socket. *You* decide what "delivered" and "lost" mean (reliable-UDP acks, app-level acks, a StateSync snapshot-ack gap), which makes it usable over any transport.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Congestion
```

## Usage

```csharp
var cc = new CongestionController(startRate: 128_000);   // 128 KB/s

// from your ack/loss tracking:
cc.OnDelivered();   // additive increase: rate += increaseBytesPerAck
cc.OnLoss();        // multiplicative decrease: rate *= decreaseFactor (default ×0.7)

// current rate, if you want to inspect it:
double bps = cc.RateBytesPerSecond;

// each tick, turn the rate into a byte budget for a priority flush:
const double tickRate = 30.0;                        // ticks/sec
int budget = cc.BudgetForInterval(1.0 / tickRate);   // bytes allowed this tick
await prioritySender.FlushAsync(budget);
```

## Constructor & API

```csharp
new CongestionController(
    double startRate           = 64_000,     // initial target rate, bytes/sec
    double minRate             = 8_000,      // floor the rate never drops below
    double maxRate             = 8_000_000,  // ceiling the rate never rises above
    double increaseBytesPerAck = 2_000,      // additive step per OnDelivered()
    double decreaseFactor      = 0.7);       // multiplier per OnLoss() (clamped to 0.1..0.99)
```

| Member | Purpose |
|---|---|
| `void OnDelivered()` | signal a successful delivery/ack — additive increase |
| `void OnLoss()` | signal a detected loss/timeout — multiplicative decrease (`× decreaseFactor`) |
| `double RateBytesPerSecond` | the current target send rate |
| `int BudgetForInterval(double seconds)` | payload bytes allowed in an interval of the given length — feed to `PrioritySender.FlushAsync` |

The rate is always clamped to `[minRate, maxRate]`, and `decreaseFactor` is clamped to `0.1..0.99` so a bad argument can't stall or disable the controller.

## End-to-end: adaptive, prioritized sending

Congestion decides *how much* to send; [`SetNet.Priority`](https://www.nuget.org/packages/SetNet.Priority) decides *what* to send first. Together they let a connection stay responsive under load — important messages keep flowing while low-priority ones defer.

```csharp
var sender = new PrioritySender(client);
var cc     = new CongestionController(startRate: 128_000);

// wire your ack/loss detection into the controller:
void OnAck()  => cc.OnDelivered();
void OnDrop() => cc.OnLoss();

const double tickRate = 30.0;

// each tick:
sender.Enqueue((ushort)MsgType.Hit,      SetNetSerializer.Serialize(hit), priority: 100, DeliveryMethod.Reliable);
sender.Enqueue((ushort)MsgType.Position, SetNetSerializer.Serialize(pos), priority: 10,  DeliveryMethod.Unreliable);

int budget = cc.BudgetForInterval(1.0 / tickRate);
await sender.FlushAsync(budget);   // high-priority first; low-priority backs up when the rate drops
```

When losses appear, `OnLoss()` shrinks the rate, `BudgetForInterval` returns fewer bytes, and the priority queue sheds its low-priority tail (watch `sender.QueuedBytes`). As acks recover, `OnDelivered()` grows the rate back up.

## Notes

- **You define the signals.** The controller has no idea what a "loss" is — call `OnLoss()` from reliable-UDP retransmit detection, an app-level ack timeout, or a StateSync snapshot gap, whatever fits your protocol.
- **Thread-safe.** All rate mutations and reads are internally locked, so ack/loss callbacks and the tick loop can run on different threads.
- **AIMD, not a full stack.** This is intentionally a small controller — a single adaptive rate. It doesn't do RTT estimation, pacing between packets, or fairness across connections; layer those on top if you need them.
- **Bytes, not packets.** The budget is a byte count meant to feed `PrioritySender.FlushAsync`, so keep your enqueued payloads sized in bytes too.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
