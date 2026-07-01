<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Fragmentation

**Send messages larger than a datagram over UDP with [SetNet](https://www.nuget.org/packages/SetNet).**

UDP is a datagram transport: a single send has to fit in one packet (SetNet caps a UDP datagram at `UdpMaxDatagramPayload`, default 1200 B, and rejects anything larger — there is no built-in UDP fragmentation). If you need to send something bigger than the MTU over UDP — a large snapshot, an inventory dump, a chunk of level data — this package splits it into numbered fragments and **reassembles them transparently on the far side**, delivering the whole message to its normal typed handler.

This is a **UDP-only** concern. TCP and WebSockets are streams and already carry any size, so you don't need this package for them.

Added by **composition** — no base class, works alongside your regular messages.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Fragmentation
```

## Setup

```csharp
FragmentationRuntime.Enable();   // once at startup, BOTH ends, before creating client/server
```

`Enable()` makes the reassembly handlers discoverable by reflection-based registration. On the client, also opt in to reassembly once after constructing it:

```csharp
client.UseFragmentation();
```

(The server reassembles per-peer automatically once `Enable()` has run — no extra call.)

## Usage

Send with a `SendFragmentedAsync` overload instead of `SendAsync`. If the payload fits within `maxChunk`, it's sent whole (one message, no overhead); otherwise it's split, sent as fragments, and reassembled before the receiver's handler runs.

### Typed (recommended)

The typed overload serializes via `SetNetSerializer` and delivers to the **normal** `IClientMessageHandler<T>` / `IServerMessageHandler<T>` for `type` — the receiving side doesn't know or care that the message was fragmented.

```csharp
// client -> server
await client.SendFragmentedAsync(
    (ushort)MessageTypes.WorldSnapshot,
    snapshot,                       // your DTO
    DeliveryMethod.Reliable);

// server -> a specific peer
await peer.SendFragmentedAsync(
    (ushort)MessageTypes.Inventory,
    inventory,
    DeliveryMethod.Reliable);
```

The handler is exactly the one you'd write for a normal message:

```csharp
[MessageHandler((ushort)MessageTypes.WorldSnapshot)]
public class WorldSnapshotHandler : IServerMessageHandler<WorldSnapshot>
{
    public Task HandleAsync(BasePeer peer, WorldSnapshot msg) { /* ... */ return Task.CompletedTask; }
}
```

### Raw bytes

If you already have serialized bytes, use the `byte[]` overload:

```csharp
await client.SendFragmentedAsync((ushort)MessageTypes.Blob, bytes, DeliveryMethod.Reliable, maxChunk: 1100);
```

## API

| Member | Side | Purpose |
|---|---|---|
| `FragmentationRuntime.Enable()` | both | make the reassembly handlers discoverable (once, at startup) |
| `client.UseFragmentation()` | client | opt the client into reassembling incoming fragments |
| `client.SendFragmentedAsync(type, byte[] payload, delivery, maxChunk = 1100)` | client | send raw bytes, fragmenting if oversize |
| `client.SendFragmentedAsync<T>(type, T msg, delivery, maxChunk = 1100)` | client | serialize + send, reassembled to the typed handler for `type` |
| `peer.SendFragmentedAsync(type, byte[] payload, delivery, maxChunk = 1100)` | server | send raw bytes to one peer |
| `peer.SendFragmentedAsync<T>(type, T msg, delivery, maxChunk = 1100)` | server | serialize + send to one peer |

`maxChunk` is the maximum fragment payload size in bytes (default **1100**, chosen to stay under a typical MTU). A message of `N` bytes becomes `ceil(N / maxChunk)` fragments; if `N <= maxChunk` it's sent whole.

## Notes

- **Prefer reliable delivery.** Reassembly needs *every* fragment — over `DeliveryMethod.Unreliable`, a single lost fragment loses the entire message (the partial set eventually times out and is dropped). Use `DeliveryMethod.Reliable` unless the whole payload is genuinely discardable.
- **Bounded reassembly.** Incoming partials are capped by an in-flight limit (256) and a staleness timeout (10 s). Never-completed sets — from loss or an abusive sender — are swept so they can't leak memory; if the cap is still hit, the oldest partial is dropped.
- **Fragment header** is 10 bytes per fragment (`[4 msgId][2 origType][2 index][2 count]`), so keep `maxChunk` well under the MTU to leave room for it and the transport headers.
- **UDP only.** Over TCP/WebSockets just use `SendAsync` — the stream already handles arbitrary sizes.
- Reserved wire type **65517**. Serializer-agnostic (fragments ride as `byte[]`); the reassembled message is delivered fully typed.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
