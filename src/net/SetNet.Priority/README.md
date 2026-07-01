<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Priority

**Priority send queue for [SetNet](https://www.nuget.org/packages/SetNet).**

When a connection is saturated, not all messages are equally important. A hit confirmation or a chat line matters more than the umpteenth position update. `PrioritySender` is a per-connection outbound queue: during a tick you **enqueue** messages with a numeric priority, then **flush** — the queue drains **highest-priority-first**. An optional per-flush **byte budget** lets low-priority traffic defer under load; whatever doesn't fit stays queued for the next flush (natural back-pressure). That budget is the hook that pairs with [`SetNet.Congestion`](https://www.nuget.org/packages/SetNet.Congestion).

Added by **composition** — construct one over a client or a peer, no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Priority
```

## Usage

Payloads are sent already-serialized (raw), so serialize once and enqueue the bytes.

```csharp
var sender = new PrioritySender(client);   // or new PrioritySender(peer)

// during your tick — enqueue serialized payloads with a priority (higher = sent sooner):
sender.Enqueue((ushort)MsgType.Hit,
               SetNetSerializer.Serialize(hit),
               priority: 100, DeliveryMethod.Reliable);

sender.Enqueue((ushort)MsgType.Position,
               SetNetSerializer.Serialize(pos),
               priority: 10,  DeliveryMethod.Unreliable);

// flush everything, highest-priority-first:
int sent = await sender.FlushAsync();

// …or flush within a byte budget — the rest stays queued for next tick:
int sentThisTick = await sender.FlushAsync(maxBytes: 30_000);

// inspect back-pressure:
Console.WriteLine($"still queued: {sender.QueuedBytes} bytes");
```

## API

| Member | Purpose |
|---|---|
| `new PrioritySender(BaseClient client)` | wrap a client's raw send path |
| `new PrioritySender(BasePeer peer)` | wrap a server peer's raw send path |
| `void Enqueue(ushort type, byte[] payload, int priority, DeliveryMethod delivery = Reliable)` | queue an already-serialized message; higher `priority` is sent first |
| `Task<int> FlushAsync(int? maxBytes = null)` | send highest-priority-first, return the count sent; with a budget, stop once `maxBytes` payload bytes are sent and leave the rest queued |
| `int QueuedBytes` | total bytes currently queued across all priorities |

## Combining Priority + Congestion

`PrioritySender` supplies the *ordering* and the back-pressure knob; [`SetNet.Congestion`](https://www.nuget.org/packages/SetNet.Congestion) supplies the *number* — a target send rate that adapts to acks and losses. Feed the controller's per-tick budget straight into `FlushAsync`:

```csharp
var sender = new PrioritySender(client);
var cc     = new CongestionController(startRate: 128_000);   // 128 KB/s

// from your own ack/loss tracking:
void OnAck()  => cc.OnDelivered();   // additive increase
void OnDrop() => cc.OnLoss();        // multiplicative decrease

const double tickRate = 30.0;        // ticks/sec

// each tick:
sender.Enqueue((ushort)MsgType.Hit,      SetNetSerializer.Serialize(hit), priority: 100);
sender.Enqueue((ushort)MsgType.Position, SetNetSerializer.Serialize(pos), priority: 10, DeliveryMethod.Unreliable);

int budget = cc.BudgetForInterval(1.0 / tickRate);
await sender.FlushAsync(budget);      // high-priority goes first; low-priority defers when the rate is low
```

Under congestion the rate falls, the budget shrinks, and low-priority messages naturally back up (visible via `QueuedBytes`) while the important ones keep flowing.

## Notes

- **Ordering is by priority, then FIFO within a priority.** Same-priority messages flush in enqueue order.
- **Always sends at least one.** A flush with a budget will send at least the top item even if it exceeds the budget, so a single large high-priority message never stalls forever.
- **Sends are raw** (`SendRawAsync`) — serialize with `SetNetSerializer` (or your own serializer) before enqueuing.
- **Not a transport.** It sits on top of SetNet's normal send path and works over any transport (TCP / UDP / Both / WebSockets); `DeliveryMethod` is honored per message as usual.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
