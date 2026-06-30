# SetNet Performance and Scaling

This document covers: (1) the performance model and how to tune it, (2) scaling limits and overload protection, (3) a structural roadmap of deferred optimizations.

The numbers below are from the built-in benchmark (`dotnet run -c Release --project SetNet.Tests -- bench`, in-process loopback, ServerGC), and are approximate.

---

## 1. Throughput vs latency

| Configuration | Throughput (1 connection) | Latency | When to use |
|---|---|---|---|
| **Default** (`TcpNoDelay = true`, no batching) | ~230k msgs/sec | lowest (each message goes out immediately) | realtime, small infrequent messages, latency-sensitive |
| **Batching** (`SendBatching = true`) | **~1.6M msgs/sec** | low (one write per flush) | game-tick, high message rate |
| Nagle on (`TcpNoDelay = false`) | ~620k msgs/sec | up to ~40 ms (Nagle+delayed-ACK) | bulk stream of small messages with no latency requirements |

**Key point:** the default `TcpNoDelay = true` optimizes for **latency** — every small message is sent immediately (no Nagle coalescing), so a "raw" stream of un-batched messages is slower. For **high throughput, enable `SendBatching`**: it coalesces a tick's messages into a single socket write, giving both high throughput and low latency (~1.6M/sec). This is the recommended path for high message rates.

```csharp
// High rate (game-tick): batch per tick, flush once
var config = new Configuration { SendBatching = true, SendBatchFlushMs = 15 };
// ... per tick:
foreach (var msg in tickMessages) await SendAsync(type, msg);
await FlushAsync(); // one socket write
```

### Memory
~10 KB per endpoint (in-process, both ends); 2000 connections are established in ~110 ms. The throughput figures include serialization — the library deserializes each inbound message into the handler's typed `T`.

### Hot paths (where nothing extra is allocated)
- **Sending:** the frame is written into a buffer from `ArrayPool`; `SendTimeoutMs` arms its timer **lazily** — only if `WriteAsync` did not complete synchronously (a rare case under back-pressure), so a typical send allocates no timer.
- **Reliability tick:** on a "quiet" channel the tick is skipped entirely; when there are unacked items, a scratch list is reused (no per-tick `List` allocation).
- **Scheduler:** `TimerScheduler` reads the clock **once** per tick (not a syscall on every registration).

---

## 2. Scaling limits and overload protection

### Bounded inbound queue (`MaxInboundQueue`, default 16384)
Protection against OOM when a peer sends faster than handlers consume:
- **Unreliable (UDP)** messages over the limit are **dropped** (best-effort, correctly), counted in `Metrics.InboundDropped`.
- **The reliable stream** on overflow **drops the connection** (rather than silently losing ordered data — this is better than corruption or OOM).
- `0` disables the limit.

### What else is bounded
- `MaxConnectionsLimit` / `MaxUdpPeers` — connection / UDP-peer ceilings.
- `MaxMessageSize` — TCP frame ceiling (slow-loris/OOM).
- `MaxConnectionsPerIpPerSecond` — per-IP rate limit (with eviction of idle windows).
- `MaxInFlightMessages` — back-pressure on handlers (the TCP receive-loop is paused).

### Known ceilings (architectural)
- **One UDP socket per server** + a single `_sendLock` serializes all UDP sends for all peers. A single receive-loop reads all datagrams. This is the server-side UDP throughput ceiling (see roadmap S2/S3).
- **`TimerScheduler`** — a single O(N) loop every 5 ms; with tens of thousands of registrations it's worth switching to a timing-wheel (roadmap P3+).
- **Default fire-and-forget dispatch** (`MaxInFlightMessages = 0`) does not bound the number of concurrent handler Tasks — for production servers, set it >0.

---

## 3. Structural roadmap (deferred optimizations)

These changes have good potential but touch the public API or are large refactors, so they're kept separate to avoid destabilizing the current (fully tested) codebase. The recommended approach for each:

### P4/P5 — Zero-alloc serialization and receive (the biggest lever for GC)
Today every message allocates: a `byte[]` from `ISerializer.Serialize` (send) and a `byte[]` payload copy in `PacketBuilder.TryGetCompleteMessage` (receive).
- **Send:** extend `ISerializer`/`ITransportConnection.SendAsync` to work with `IBufferWriter<byte>`/`ReadOnlyMemory<byte>` into a pooled buffer, pass the slice, return the buffer after framing. This removes the serialization allocation (for MessagePack — via `MessagePackSerializer.Serialize(IBufferWriter<byte>, …)`).
- **Receive:** for the `SequentialDispatch`/`MaxInFlightMessages>0` modes (where the receive-loop tracks handler completion) — rent the payload from `ArrayPool` and pass `ReadOnlyMemory<byte>`, return it after dispatch. This requires changing the handler contract (`byte[]` → `ReadOnlyMemory<byte>`).
- **Risk:** changes the transport core API + the handler contract. Do it as a separate version (minor bump) with a migration guide.

### S2/S3 — Scaling the UDP socket
- Remove the user-space `_sendLock` by using `Socket.SendToAsync` with a pool of `SocketAsyncEventArgs` (kernel-safe for concurrent sends), and run several concurrent `ReceiveAsync` on the read-loop.
- Alternative: shard across multiple sockets (complicated by the single handshake port on netstandard2.1 — requires SO_REUSEPORT, which isn't portable).
- **Risk:** concurrency on a shared socket; thorough testing under loss/both.

### S4 — Concurrent accept + TLS handshake
Today accept and the TLS handshake are serialized in a single loop (DoS is closed off by a timeout, but accept throughput is bounded). Split them: a tight accept-loop hands the socket to a bounded worker pool, which does the handshake + `OnNewClient`. **Risk:** concurrency, pool bounds.

### P3+ — TimerScheduler timing-wheel
Replace the O(N) scan every 5 ms with a timing-wheel or a min-heap keyed on `DueTimestamp`, so each tick touches only the due entries. **Risk:** concurrency of Schedule/Unschedule with the structure (the current `ConcurrentDictionary` is trivially thread-safe).

### S5 — Back-pressure default
Consider a non-zero default for `MaxInFlightMessages` (or a loud warning), so a production server doesn't spawn unbounded Tasks by default.
