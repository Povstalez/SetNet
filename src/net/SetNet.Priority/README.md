<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Priority

**Priority send queue for [SetNet](https://www.nuget.org/packages/SetNet).**

Enqueue outbound messages with a priority during a tick, then flush **highest-priority-first**. An optional per-flush **byte budget** lets low-priority traffic defer under load — the hook that pairs with [`SetNet.Congestion`](https://www.nuget.org/packages/SetNet.Congestion).

## Install & use

```bash
dotnet add package SetNet
dotnet add package SetNet.Priority
```

```csharp
var sender = new PrioritySender(client);   // or new PrioritySender(peer)

// during your tick — enqueue already-serialized payloads with a priority:
sender.Enqueue(MsgType.Hit,      SetNetSerializer.Serialize(hit),  priority: 100, DeliveryMethod.Reliable);
sender.Enqueue(MsgType.Position, SetNetSerializer.Serialize(pos),  priority: 10,  DeliveryMethod.Unreliable);

// flush (optionally within a byte budget — the rest stays queued):
await sender.FlushAsync(maxBytes: 30_000);
```

Higher priority is sent first; when a byte budget is set, whatever doesn't fit remains queued for the next flush.

## License

MIT · [github.com/Povstalez/SetNet](https://github.com/Povstalez/SetNet)
