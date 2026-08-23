# SetNet Performance and Scaling

This document covers: (1) the performance model and how to tune it, (2) scaling limits and overload protection, (3) a structural roadmap of deferred optimizations.

The numbers below are from the built-in benchmark (`dotnet run -c Release --project tests/SetNet.Tests -- bench`, in-process loopback, ServerGC), and are approximate.

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
- **Client push events (since 1.3.0):** delivering an event to a typed `On<T>` subscriber allocates nothing beyond the decoded message itself — see below.

### Client push-event delivery (1.3.0)

Push events are the one path a game client walks hundreds of times per *frame*, so three allocations that are
invisible in request/response traffic added up to a steady garbage stream there. All three are gone:

| Was | Now |
|---|---|
| `ClientEventDiscovery` called `AppDomain.CurrentDomain.GetAssemblies()` on **every dispatch** just to compare the count — and that call builds a fresh array holding every loaded assembly | The scan state subscribes to `AppDomain.AssemblyLoad` once and raises a flag; the steady state is one `volatile bool` read. A module enabled later is still picked up — the CLR reports its assembly. |
| `foreach (var cb in bucket.Values)` — `ConcurrentDictionary.Values` builds a snapshot list per access | A copy-on-write snapshot array walked with a plain `for` — see the correction under 1.6.2 below |
| `ProtocolEnvelope.Decode` copied the body out of the received frame for every envelope | The dispatcher reads only the header and hands an event body to subscribers as a `ReadOnlyMemory<byte>` window onto the frame. Replies still get an array of their own — they outlive the call. |

To benefit, the registered serializer must also implement `IMemorySerializer` (the bundled
`MessagePackNetSerializer` does). `On<T>` then subscribes through `ProtocolSubscriptionRegistry.AddMemory` and
decodes in place. With any other serializer it falls back to the array path, unchanged — the choice is invisible
to callers, who see the same decoded message either way.

`OnRaw` subscribers still receive a `byte[]`, exactly as before — but the array is now materialised **lazily**,
only when such a subscriber actually exists for that channel/op. Every SetNet module (Ping, Relay, NatPunch,
Trade, Rooms…) subscribes with `OnRaw`, so their behaviour is untouched.

**Rule for memory-based subscribers:** the window is valid only for the duration of the callback. Decode from it;
do not store it. A subscriber that needs to keep the bytes should use `Add`/`OnRaw` and get its own array.

### Client receive path (1.6.2 / 1.6.3)

Three more allocations per inbound message, found by profiling a Unity client taking hundreds of events per frame.

**A correction to the 1.3.0 note above.** That release replaced `bucket.Values` with a `foreach` over the
`ConcurrentDictionary` itself, on the grounds that its enumerator is a struct. **That is true on .NET 9+ and false
on the Mono runtime Unity ships**, where `GetEnumerator()` returns an interface backed by a compiler-generated
class — one heap allocation per delivered event, which for a small event is more than the payload. Since 1.6.2 the
bucket keeps a copy-on-write snapshot array beside the dictionary and delivery walks it with a plain `for`, which
allocates nothing on any runtime. The snapshot is rebuilt only on subscribe/unsubscribe and published with
`Volatile.Write`, so a subscription taken out mid-dispatch is still tolerated: the walker finishes with the list it
started on.

**The envelope was copied twice, not once.** 1.3.0 removed the copy inside `ProtocolEnvelope.Decode`, but one level
up `CommandExecutor.DispatchAsync` still called `Deserialize<byte[]>` on the whole frame — producing a second array
holding byte for byte what the transport already held, minus the wrap header. Since 1.6.2 the header is skipped in
place and the dispatcher takes a `ReadOnlyMemory<byte>` window (`DispatchClientAsync(SetNetRuntime,
ReadOnlyMemory<byte>)`). The header length comes from `IBinaryFrameSerializer.MeasureBinaryFrameHeader`, so format
knowledge stays inside the serializer; a serializer that does not implement it keeps the old path unchanged.
**Replies and errors still get an array of their own** — they are handed to a caller waiting on another thread, and
that copy is the ownership boundary, not an oversight.

**`ReceiveAsync` returns `ValueTask` (1.6.3).** During a burst the next frame is usually already in the receive
buffer, so the call completes synchronously — and a `Task`-returning method allocates on that path regardless: the
BCL caches Task results only for `bool`, small `int` and null references, and a `TransportMessage?` with a value is
none of those. The usual `ValueTask` rules apply and are not optional: await it once, do not store it, do not
combine it with `Task.WhenAny`; call `AsTask()` first if you need any of that.

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
- **Partly done in 1.6.2/1.6.3** (see the client receive path above): the envelope copy and the `Task` per received
  message are gone. What remains is the `byte[]` in `PacketBuilder`, and it is the hard half — there is no safe
  point to return a pooled buffer today, for two independent reasons. `BaseSocket.DispatchAsync` hands the array to
  `OnRawFrame`, whose contract promises *"a fresh per-message array; safe to keep or forward"* — and that is load
  bearing: `SetNet.Gateway` forwards it byte for byte, `SetNet.Cluster` too. And with the default
  `SequentialDispatch = false`, handlers are dispatched without waiting, so nothing knows when the array is free.
  An opt-in flag is not an acceptable shortcut here: getting it wrong does not throw, it silently overwrites
  another message. Pooling the receive buffer needs **explicit buffer ownership through the whole dispatch chain**,
  which is a design decision, not a micro-optimization.

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
