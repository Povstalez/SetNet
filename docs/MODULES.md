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
| **SetNet.WebSockets** | WebSocket transport via `config.UseWebSockets()` (`ClientWebSocket` + `HttpListener`) | uses the custom-transport hook |
| **SetNet.RateLimit** | per-peer token-bucket inbound gate (`server.UseRateLimit(...)`) | uses `InboundAuthorizer`, chains with Auth |
| **SetNet.Unity** | `MainThreadDispatcher` to marshal handler callbacks onto Unity's main thread | WebGL unsupported |
| **SetNet.Logging.Serilog** | `SerilogLogger : ILogger` routing diagnostics into Serilog | `config.Logger = new SerilogLogger(...)` |

### Core extension points already in place (for composition packages)
- `SetNetSerializer.Use/Serialize/Deserialize` — pluggable serialization.
- Auto-discovered typed **and** `byte[]` handlers (`IServer/ClientMessageHandler<T>`) via `[MessageHandler]`.
- `BaseClient/BasePeer.SendAsync<T>` and `SendRawAsync` — **public**.
- `BaseSocket.OnRawFrame` (intercept/consume) + `SendRawAsync` — relay/proxy primitive.
- `BaseServer.InboundAuthorizer` + `BaseSocket.AllowInbound` — per-frame inbound gate.
- `BaseClient.Connected` event (after connect+reconnect), `BaseServer.PeerDisconnected` event (once per peer).
- Public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`.
- **Custom transport registration** — `TransportType.Custom` + `Configuration.CustomTransport : ITransportProvider`,
  resolved in `TransportFactory`. Lets an external package supply `ITransportConnector`/`ITransportListener` without
  editing core (used by **SetNet.WebSockets**; also the path for future **SetNet.WebRTC** / **SetNet.Quic** / **SetNet.Steam**).

---

## Planned / future ideas

- **SetNet.Logging.MicrosoftExtensions** — an `ILogger` adapter over `Microsoft.Extensions.Logging` (sibling to the Serilog package).
- **SetNet.WebRTC / SetNet.Quic / SetNet.Steam** — additional transports via the same `ITransportProvider` hook.
- **SetNet.RateLimit** per-message-type budgets (`PerType = {...}`) on top of the current per-peer bucket.
