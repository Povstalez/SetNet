<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Streams

**Large-payload streaming for [SetNet](https://www.nuget.org/packages/SetNet): patches, avatars, replays — offered, chunked, resumable.**

Regular messages are for kilobytes; this package is for the megabytes. A transfer starts with an **offer** (name + size) the receiver can accept or reject, then flows as sequential chunks into a pluggable **sink** (memory or file), with **progress** on the sender and **resume** after a dropped connection — an interrupted upload retried with the same id re-sends only the missing tail. Works in both directions (client → server uploads, server → client pushes). Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Streams
```

## Usage

Call `StreamsRuntime.Enable()` once at startup on both ends (before handler discovery).

**Server** — receive uploads:

```csharp
StreamsRuntime.Enable();
var streams = server.UseStreams();

streams.Received += (peer, s) =>
{
    var bytes = ((MemoryStreamSink)s.Sink).ToArray();
    Console.WriteLine($"{peer.CurrentPeerInfo.Id} sent '{s.Name}' ({s.Length} B)");
};
```

**Client** — upload with progress and resume:

```csharp
StreamsRuntime.Enable();
var streams = client.UseStreams();

var progress = new Progress<double>(p => DrawBar(p));
try
{
    await streams.SendAsync("replay.dat", File.OpenRead("replay.dat"), progress);
}
catch (StreamsException ex)          // connection dropped mid-upload
{
    await client.ConnectAsync();
    await streams.SendAsync("replay.dat", File.OpenRead("replay.dat"), progress,
                            streamId: ex.StreamId);   // resumes — only the tail is re-sent
}
```

**Large / sensitive offers** — decide per offer, stream to disk:

```csharp
streams.OfferReceived += async offer =>
{
    if (offer.Length > 1_000_000_000) { await offer.RejectAsync("Too big."); return; }
    await offer.AcceptAsync(new FileStreamSink($"incoming/{offer.StreamId}.bin"));
};
```

**Server → client** works the same: `await streams.SendAsync(peer, "map.pak", content)`.

## API

| Member | Purpose |
|---|---|
| `server.UseStreams(StreamsOptions?)` → `StreamsServer` | hub: `SendAsync(peer, ...)` + `OfferReceived`/`Received` events (per peer) |
| `client.UseStreams(StreamsOptions?)` → `StreamsClient` | driver: `SendAsync(...)` + `OfferReceived`/`Received` events |
| `SendAsync(name, Stream/byte[], IProgress<double>?, Guid? streamId, ct)` | offer + chunked upload; returns the transfer id; same id ⇒ resume |
| `IncomingStreamOffer.AcceptAsync(IStreamSink?)` / `RejectAsync(reason)` | per-offer decision (default sink = memory) |
| `IStreamSink` / `MemoryStreamSink` / `FileStreamSink` | where the bytes go |
| `StreamsOptions` | `AutoAccept` (default on, ≤ `MaxAutoAcceptBytes` 64 MB), `ChunkSize` (64 KB), `OfferTimeoutMs`, `PartialTtlSeconds` |
| `StreamsRuntime.Enable()` | one-time bootstrap so the handlers are discovered |

## Notes

- **Reserved wire types 65492 / 65493.** Don't reuse them.
- **Needs a reliable, ordered path.** Chunks are validated to be exactly contiguous; run over TCP / WebSockets / UDP-reliable. Over reliable UDP set `ChunkSize` ≈ 1 KB (datagram limit); over TCP keep it well under `Configuration.MaxMessageSize`.
- **Resume is receiver-driven.** The receiver keeps interrupted partials for `PartialTtlSeconds` keyed by stream id; the accept reply tells the sender where to continue. Use a `FileStreamSink` for partials that must survive a receiver restart.
- **Back-pressure.** Chunks are awaited one at a time through the normal send path, so `MaxInFlightMessages`/`SendTimeoutMs` apply; a stream saturates the connection while active — pair with [`SetNet.Multiplex`](https://www.nuget.org/packages/SetNet.Multiplex) to keep latency-critical traffic on its own dispatch lane.
- **Trust.** An offer's size cap is enforced (`MaxAutoAcceptBytes`, overrun checks), but accept-all-from-anyone is still a storage giveaway — gate with [`SetNet.Auth`](https://www.nuget.org/packages/SetNet.Auth) and decide in `OfferReceived` for anything user-generated.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
