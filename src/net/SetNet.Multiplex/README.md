<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Multiplex

**Logical channels over one [SetNet](https://www.nuget.org/packages/SetNet) connection — fix head-of-line blocking without a second socket.**

On an ordered transport everything shares one delivery order: while a big state dump is being handled, the chat message and the input packet queued behind it wait. `SetNet.Multiplex` wraps sends in a 3-byte envelope (`[channel][originalType]`) and, on the receiving side, dispatches **each channel on its own ordered lane** — frames within a channel stay in order, channels never block each other, and the original typed handlers fire exactly as if the frame had arrived unwrapped. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Multiplex
```

## Usage

Call `MultiplexRuntime.Enable()` once at startup on both ends (before handler discovery). Receivers opt in with `UseMultiplex()`:

```csharp
MultiplexRuntime.Enable();
client.UseMultiplex();          // client receives demuxed frames
// the server side needs no opt-in — its handler demuxes per peer automatically
```

Send on channels — same types, same handlers, just a channel id in front:

```csharp
// client → server
await client.SendMuxAsync(channel: 0, (ushort)MessageTypes.PlayerInput, input);   // latency-critical lane
await client.SendMuxAsync(channel: 1, (ushort)MessageTypes.ChatLine, chat);       // chat lane
await client.SendMuxAsync(channel: 2, (ushort)MessageTypes.MapChunk, bigBlob);    // bulk lane

// server → client
await peer.SendMuxAsync(channel: 2, (ushort)MessageTypes.WorldSnapshot, snapshot);
```

A slow `MapChunk` handler no longer delays `PlayerInput` — they live on different lanes.

## API

| Member | Purpose |
|---|---|
| `client.UseMultiplex()` | register a client for per-channel demux of incoming frames |
| `client.SendMuxAsync(byte channel, ushort type, T message, DeliveryMethod = Reliable)` | send a typed message on a channel |
| `peer.SendMuxAsync(byte channel, ushort type, T message, DeliveryMethod = Reliable)` | server → client equivalent |
| `SendMuxAsync(channel, type, byte[] payload, ...)` | raw overloads for already-serialized bytes |
| `MultiplexRuntime.Enable()` | one-time bootstrap so the handlers are discovered |

256 channels (`byte`). Messages sent with plain `SendAsync` are unaffected and keep the global order.

## Notes

- **Reserved wire type 65494.** Don't reuse it.
- **Ordering semantics.** Within a channel, handlers are *started* in arrival order (same semantics as core dispatch); across channels there is no ordering at all — that's the point. If a handler must fully complete before the next frame of its channel is processed, do the completion-ordering inside your handler (or keep the handler synchronous).
- **Don't tunnel reserved types.** The envelope's inner type should be an application type — wrapping other modules' reserved frames (RPC, rooms, mux itself) is undefined.
- **Complementary to UDP channels.** `Configuration.UdpReliableChannels` separates *retransmission* streams on the wire; Multiplex separates *dispatch* lanes above any transport (TCP, WebSockets, UDP-reliable). They compose.
- **Co-located clients** share demux routing (frames reach every registered client in the process) — one client per process is the typical shape, same as Fragmentation/Rooms.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
