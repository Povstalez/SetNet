# SetNet modules — implemented & planned

SetNet is a small core plus optional **companion packages** added by composition. Each depends only on `SetNet`
(unless noted) and is wired by an `xxxRuntime.Enable()` call + `server.UseXxx()` / `client.UseXxx()` extensions,
so the core stays lean and users pull only what they need.

## Shipped

Grouped by purpose (mirrors the `src/<category>/` layout). Each depends only on `SetNet` unless noted.

**Serializers** (`src/serializers/`)
| Package | What it adds |
|---|---|
| **SetNet.MessagePack** | `MessagePackNetSerializer` (`ISerializer`, `UntrustedData`-hardened) — the recommended serializer |
| **SetNet.Json** | `System.Text.Json` serializer — human-readable, web-friendly |
| **SetNet.MemoryPack** | MemoryPack serializer — fast, zero-encoding, AOT/IL2CPP-friendly |
| **SetNet.Protobuf** | protobuf-net serializer — compact, cross-language |
| **SetNet.Compression** | decorator `ISerializer` wrapping any inner one with transparent Brotli |

**Transports** (`src/transports/`)
| Package | What it adds |
|---|---|
| **SetNet.WebSockets** | WebSocket transport via `config.UseWebSockets()` |
| **SetNet.InMemory** | in-process loopback transport via `config.UseInMemory()` (no sockets; great for tests) |

**Messaging** (`src/messaging/`)
| Package | What it adds |
|---|---|
| **SetNet.Rpc** | `await client.CallAsync<TReq,TResp>()` + `[RpcMethod]` handlers |

**Security** (`src/security/`)
| Package | What it adds |
|---|---|
| **SetNet.Auth** | enforced gate + token auth + sessions (TTL, rotation, sweep) + `ISessionStore` + reconnect-resume |
| **SetNet.Auth.Jwt** | JWT `IAuthenticator` (deps `SetNet.Auth`) |
| **SetNet.Auth.OAuth** | OpenID Connect authenticator (auto-refreshed JWKS) (deps `SetNet.Auth`) |
| **SetNet.BanList** | inbound gate dropping banned peers (IP/custom key) + kick; pluggable `IBanStore` |
| **SetNet.DdosGuard** | per-IP connection-flood detection + timed auto-ban (deps `SetNet.BanList`) |

**Net / QoS** (`src/net/`)
| Package | What it adds |
|---|---|
| **SetNet.RateLimit** | per-peer token-bucket inbound gate |
| **SetNet.Fragmentation** | split/reassemble oversize UDP messages transparently |
| **SetNet.Priority** | priority send queue with an optional per-flush byte budget |
| **SetNet.Congestion** | AIMD congestion controller → per-tick byte budget |

**Realtime / game** (`src/realtime/`)
| Package | What it adds |
|---|---|
| **SetNet.Rooms** | rooms/lobbies by code, broadcast, join/leave events + `IRoomStore` |
| **SetNet.Rooms.HostMigration** | designate + migrate room host on leave (deps `SetNet.Rooms`) |
| **SetNet.Matchmaking** | FIFO/skill queues with a widening window → creates a room (deps `SetNet.Rooms`) |
| **SetNet.Party** | party/group by code, leader + ready state, events |
| **SetNet.Chat** | channel-based text chat + moderation |
| **SetNet.Lockstep** | deterministic wait-for-all turn engine |
| **SetNet.StateSync** | server-authoritative entity replication: delta snapshots, interpolation, interest, input |
| **SetNet.StateSync.SpatialGrid** | grid interest manager (deps `SetNet.StateSync`) |
| **SetNet.StateSync.LagCompensation** | server rewind for fair hit detection (deps `SetNet.StateSync`) |
| **SetNet.StateSync.Prediction** | client input buffer + reconciliation (deps `SetNet.StateSync`) |
| **SetNet.StateSync.NetworkVariable** | typed change-tracked `NetworkVariable<T>` (deps `SetNet.StateSync`) |
| **SetNet.StateSync.Rpc** | entity-scoped RPCs both directions (deps `SetNet.StateSync`) |

**Infra** (`src/infra/`)
| Package | What it adds |
|---|---|
| **SetNet.DependencyInjection** | construct handlers via an `IServiceProvider` (`Microsoft.Extensions.DependencyInjection`) |
| **SetNet.Hosting** | run a server as an `IHostedService` (`Microsoft.Extensions.Hosting`) |
| **SetNet.HealthChecks** | `IHealthCheck` for liveness + connections (`Microsoft.Extensions.Diagnostics.HealthChecks`) |
| **SetNet.Inspector** | HttpListener dashboard (`/metrics` JSON + HTML) |
| **SetNet.Gateway** | raw-relay reverse proxy: accept clients → forward to per-client backend |

**Logging** (`src/logging/`)
| Package | What it adds |
|---|---|
| **SetNet.Logging.Serilog** | `SerilogLogger : ILogger` routing diagnostics into Serilog |

**Engine bindings** (`src/engine/`)
| Package | What it adds |
|---|---|
| **SetNet.Unity** | `MainThreadDispatcher` for Unity's main thread |
| **SetNet.StateSync.Unity** | Unity components: NetworkObject/Transform/Animator/Rigidbody/Behaviour + NetworkManager (**UPM source**, not NuGet) |
| **SetNet.Godot** | Godot 4 (C#) main-thread dispatcher + math conversions (deps `SetNet.StateSync` + `GodotSharp`) |

### Core extension points already in place (for composition packages)
- `SetNetSerializer.Use/Serialize/Deserialize` — pluggable serialization.
- Auto-discovered typed **and** `byte[]` handlers (`IServer/ClientMessageHandler<T>`) via `[MessageHandler]`.
- `BaseClient/BasePeer.SendAsync<T>` and `SendRawAsync` — **public**.
- `BaseSocket.OnRawFrame` (intercept/consume) + `SendRawAsync` — relay/proxy primitive.
- `BaseServer.InboundAuthorizer` + `BaseSocket.AllowInbound` — per-frame inbound gate.
- `BaseClient.Connected` event (after connect+reconnect), `BaseServer.PeerDisconnected` + `BaseServer.PeerConnected` events (once per peer).
- Public `BasePeer.CurrentPeerInfo` + `PeerInfo.Server`; `PeerInfo.RemoteEndPoint` + `BasePeer.RemoteEndPoint` (used by BanList/DdosGuard/Inspector/Gateway).
- `BaseSocket.InjectFrame(type, data)` — feed a reconstructed frame into normal dispatch (used by Fragmentation reassembly; also relays/testing).
- `HandlerActivator.Factory` — route handler construction through a container (used by DependencyInjection; default falls back to the parameterless ctor).
- `RoomServer.RoomHooks()` — server-side room join/leave events (used by Rooms.HostMigration).
- **Custom transport registration** — `TransportType.Custom` + `Configuration.CustomTransport : ITransportProvider`,
  resolved in `TransportFactory`. Lets an external package supply `ITransportConnector`/`ITransportListener` without
  editing core (used by **SetNet.WebSockets** / **SetNet.InMemory**; also the path for future **SetNet.WebRTC** / **SetNet.Quic** / **SetNet.Steam**).

---

## Planned / future ideas

- **SetNet.Logging.MicrosoftExtensions** — an `ILogger` adapter over `Microsoft.Extensions.Logging` (sibling to the Serilog package).
- **SetNet.WebRTC / SetNet.Quic / SetNet.Steam** — additional transports via the same `ITransportProvider` hook.
- **SetNet.RateLimit** per-message-type budgets (`PerType = {...}`) on top of the current per-peer bucket.
