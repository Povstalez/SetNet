# SetNet modules — implemented & planned

SetNet is a small core plus optional **companion packages** added by composition. Each depends only on `SetNet`
(unless noted) and is wired by an `xxxRuntime.Enable()` call + `server.UseXxx()` / `client.UseXxx()` extensions,
so the core stays lean and users pull only what they need.

## Shipped

| Package | What it adds | Notes |
|---|---|---|
| **SetNet.MessagePack** | `MessagePackNetSerializer` (`ISerializer`, `UntrustedData`-hardened) | the recommended serializer |
| **SetNet.Rpc** | `await client.CallAsync<TReq,TResp>()` + `[RpcMethod]` handlers | serializer-agnostic |
| **SetNet.Auth** | enforced gate + token auth + sessions (TTL, rotation, sweep) + `ISessionStore` + reconnect-resume | over TLS |
| **SetNet.Rooms** | rooms/lobbies by code, broadcast, join/leave events + `IRoomStore` | dedicated server, no relay |

### Core extension points already in place (for composition packages)
- `SetNetSerializer.Use/Serialize/Deserialize` — pluggable serialization.
- Auto-discovered typed **and** `byte[]` handlers (`IServer/ClientMessageHandler<T>`) via `[MessageHandler]`.
- `BaseClient/BasePeer.SendAsync<T>` and `SendRawAsync` — **public**.
- `BaseSocket.OnRawFrame` (intercept/consume) + `SendRawAsync` — relay/proxy primitive.
- `BaseServer.InboundAuthorizer` + `BaseSocket.AllowInbound` — per-frame inbound gate.
- `BaseClient.Connected` event (after connect+reconnect), `BaseServer.PeerDisconnected` event (once per peer).
- Public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`.

---

## Planned — ready to implement

### SetNet.RateLimit  ·  effort: S  ·  core hooks: none (uses `InboundAuthorizer`)
Per-peer / per-message-type rate limiting (token bucket). Drops or throttles frames that exceed a budget.
- **API:** `server.UseRateLimit(new RateLimitOptions { PerPeerPerSecond = 50, Burst = 100 /*, PerType = {...}*/ });`
- **How:** sets `BaseServer.InboundAuthorizer` (already exists) to consult a per-peer token bucket keyed by
  `peer.CurrentPeerInfo.Id`; return `false` to drop when over budget. Bucket state in a `ConditionalWeakTable<BasePeer, Bucket>`
  (auto-cleared on disconnect, like auth). Optionally increment `NetworkMetrics`.
- **Deps:** `SetNet` only. **Status:** fully unblocked — no core change.

### SetNet.Logging.Serilog  ·  effort: XS  ·  core hooks: none
A `Serilog`-backed `ILogger` so SetNet diagnostics flow into a Serilog pipeline.
- **API:** `config.Logger = new SerilogLogger(Log.Logger);`
- **How:** `SerilogLogger : SetNet.Logging.ILogger` maps `Log(message, LogLevel)` to Serilog levels. (`ILogger` is
  already pluggable via `Configuration.Logger`.) Ship siblings later: `SetNet.Logging.MicrosoftExtensions`.
- **Deps:** `SetNet` + `Serilog`. **Status:** fully unblocked; trivial.

### SetNet.Unity  ·  effort: M  ·  core hooks: none (packaged as a Unity/UPM package)
Unity ergonomics on top of the netstandard2.1 core.
- **Contents:** a **main-thread dispatcher** (`UnityDispatcher` — queue handler callbacks and drain them in
  `Update()`, since SetNet handlers run on background threads and Unity APIs are main-thread only); a
  `MonoBehaviour` client wrapper; IL2CPP/AOT setup notes (register MessagePack generated resolver, or use an
  AOT-friendly serializer); a `.unitypackage`/UPM manifest.
- **Deps:** `SetNet` (+ optionally `SetNet.MessagePack`). **Status:** unblocked; this is packaging/ergonomics, not
  a NuGet library per se. WebGL remains unsupported (no threads/sockets).

### SetNet.WebSockets  ·  effort: L  ·  core hooks: **1 (transport registration)**
A WebSocket transport so browser/Blazor/firewalled clients can connect (WS is HTTP-friendly and proxy-traversal-safe).
- **API:** `config.TransportType = TransportType.Custom; config.CustomTransport = new WebSocketTransport();` (or a
  fluent `config.UseWebSockets()`), then everything above the transport (handlers, RPC, rooms, auth) works unchanged.
- **How:** implement `ITransportConnection` / `ITransportConnector` / `ITransportListener` (all public) over
  `System.Net.WebSockets.ClientWebSocket` (client) and `HttpListener`/ASP.NET `WebSocketManager` (server), reusing
  the length-prefix framing.
- **Required core change:** today `TransportFactory` switches on the `TransportType` enum. Add a small hook to
  register a **custom transport factory** (e.g. `Configuration.CustomTransport : ITransportProvider` or a
  `TransportFactory.Register(name, connectorFactory, listenerFactory)`), so external transports plug in without
  editing core. This is the only blocker; it also unlocks **SetNet.WebRTC**, **SetNet.Quic**, **SetNet.Steam**.
- **Deps:** `SetNet` (+ server side may need `Microsoft.AspNetCore.*` or use `HttpListener`). **Status:** needs the
  transport-registration hook first, then implement + test the WS transport.

---

## Effort key
XS ≈ an afternoon · S ≈ 1 day · M ≈ few days · L ≈ 1–2 weeks (mostly WebSockets’ transport + browser testing).

## Suggested order
1. **SetNet.Logging.Serilog** (trivial, immediate ops value).
2. **SetNet.RateLimit** (no core change; abuse protection).
3. **transport-registration hook + SetNet.WebSockets** (opens the browser audience; also unlocks WebRTC/QUIC/Steam).
4. **SetNet.Unity** (packaging + main-thread dispatcher).
