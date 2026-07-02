# SetNet modules — implemented & planned

SetNet is a small core plus optional **companion packages** added by composition. Each depends only on `SetNet`
(unless noted) and is wired by an `xxxRuntime.Enable()` call + `server.UseXxx()` / `client.UseXxx()` extensions,
so the core stays lean and users pull only what they need.

**Unified messaging (`SetNet.Protocol`, in core).** Every command/reply/event companion module below speaks the
same three verbs over one shared envelope wire id (`ProtocolTypes.Envelope`), demultiplexed by a per-module
`Channels.X` id + an `op`: correlated **request/reply** (`client.RequestAsync`/`RequestRawAsync`), **fire-and-forget**
(`client.PostAsync`/`PostRawAsync`), and **server push** (`client.On<T>`/`OnRaw` ↔ `peer.PublishRawAsync`), with the
server side handled by an auto-discovered `[ProtocolChannel(Channels.X)] IChannelService`. This means no per-module
wire-type reservations and one shared correlation/subscription mechanism instead of each module hand-rolling its own.
A handful of modules with a different message shape stay off it and keep their own reserved ids: `SetNet.Rpc`,
`SetNet.StateSync` (+ `.Rpc`), `SetNet.Voice`, `SetNet.Fragmentation`, `SetNet.Multiplex`, `SetNet.Streams`,
`SetNet.Cluster`, `SetNet.Auth`, `SetNet.ProofOfWork`.

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
| **SetNet.Streams** | large-payload streaming both directions: offer/accept, sequential chunks, progress, resume after disconnect; pluggable sinks (memory/file) |

**Security** (`src/security/`)
| Package | What it adds |
|---|---|
| **SetNet.Auth** | enforced gate + token auth + sessions (TTL, rotation, sweep) + `ISessionStore` + reconnect-resume |
| **SetNet.Auth.Jwt** | JWT `IAuthenticator` (deps `SetNet.Auth`) |
| **SetNet.Auth.OAuth** | OpenID Connect authenticator (auto-refreshed JWKS) (deps `SetNet.Auth`) |
| **SetNet.BanList** | inbound gate dropping banned peers (IP/custom key) + kick; pluggable `IBanStore` |
| **SetNet.DdosGuard** | per-IP connection-flood detection + timed auto-ban (deps `SetNet.BanList`) |
| **SetNet.GeoBlock** | reject connections by country (blocklist/allowlist) via a pluggable `IGeoResolver`; kick-on-connect |
| **SetNet.ProofOfWork** | hashcash admission gate — drop a peer's frames until it solves a SHA-256 challenge (anti-bot) |

**Net / QoS** (`src/net/`)
| Package | What it adds |
|---|---|
| **SetNet.RateLimit** | per-peer token-bucket inbound gate |
| **SetNet.Fragmentation** | split/reassemble oversize UDP messages transparently |
| **SetNet.Priority** | priority send queue with an optional per-flush byte budget |
| **SetNet.Congestion** | AIMD congestion controller → per-tick byte budget |
| **SetNet.Relay** | TURN-style relay hub: allocate/join a session by code, forward opaque bytes (NAT fallback/tunnel) |
| **SetNet.NatPunch** | UDP hole-punching: coordinator exchanges public/private endpoint candidates + `NatPuncher.TryPunchAsync` opens the direct path (fallback: Relay) |
| **SetNet.Upnp** | UPnP IGD port mapping: SSDP discovery + AddPortMapping/DeletePortMapping/GetExternalIPAddress; `config.MapServerPortsAsync()` (no wire types) |
| **SetNet.Multiplex** | logical channels over one connection: `SendMuxAsync(channel, type, msg)` → per-channel ordered dispatch lanes (head-of-line-blocking fix) |

**Realtime / game** (`src/realtime/`)
| Package | What it adds |
|---|---|
| **SetNet.Rooms** | rooms/lobbies by code, broadcast, join/leave events + `IRoomStore` |
| **SetNet.Rooms.HostMigration** | designate + migrate room host on leave (deps `SetNet.Rooms`) |
| **SetNet.Matchmaking** | FIFO/skill queues with a widening window → creates a room (deps `SetNet.Rooms`) |
| **SetNet.Party** | party/group by code, leader + ready state, events |
| **SetNet.Chat** | channel-based text chat + moderation |
| **SetNet.Voice** | codec-agnostic voice-chat relay: numeric channels, opaque audio frames, unreliable fan-out |
| **SetNet.Lockstep** | deterministic wait-for-all turn engine |
| **SetNet.Inventory** | server-authoritative stackable inventory: grant/revoke by player key, atomic `TryRevoke`, read + push; pluggable `IInventoryStore` |
| **SetNet.Trade** | player-to-player escrow trading: two-phase ready→confirm lock, atomic cross-swap through Inventory with rollback (deps `SetNet.Inventory`) |
| **SetNet.Mail** | offline mail with item attachments escrowed from the sender and granted on claim; pluggable `IMailStore` (deps `SetNet.Inventory`) |
| **SetNet.Zones** | seamless player handoff between world nodes: stash carried state under a one-time token, push a migrate instruction, claim on the destination; pluggable `IHandoffStore` |
| **SetNet.Wallet** | server-authoritative currencies: atomic deposit/withdraw/transfer by player key, read + push; pluggable `IWalletStore` |
| **SetNet.Vendor** | NPC shops: buy/sell catalogs with stock, settled atomically through Wallet + Inventory (deps `SetNet.Inventory` + `SetNet.Wallet`) |
| **SetNet.Auction** | player auction house: escrow item + bids, timer-settled, outbid refunds + won/sold/returned events (deps `SetNet.Inventory` + `SetNet.Wallet`) |
| **SetNet.Crafting** | recipes (inputs → outputs) crafted atomically through Inventory with rollback (deps `SetNet.Inventory`) |
| **SetNet.Loot** | weighted server-side drop tables (guaranteed + weighted draws), granted through Inventory; gated client-open (deps `SetNet.Inventory`) |
| **SetNet.Quests** | quests with objectives + item rewards: server-driven progress, `QuestCompleted` event, claim grants via Inventory (deps `SetNet.Inventory`) |
| **SetNet.Progression** | levels/XP with a configurable curve, multi-level rollover, `LeveledUp` event, read + push; pluggable `IProgressionStore` |
| **SetNet.Guilds** | guilds/clans: roles (member/officer/leader), promote/kick, shared bank as a guild-keyed inventory (deps `SetNet.Inventory`) |
| **SetNet.Marketplace** | continuous double-sided order book: limit buy/sell, price-time matching at the resting price, escrow via Wallet + Inventory (deps `SetNet.Inventory` + `SetNet.Wallet`) |
| **SetNet.StatusEffects** | server-authoritative buffs/debuffs on any target key: timed, stacking, timer-expired, pushed to the target + watchers |
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
| **SetNet.Cluster** | server-to-server broadcast bus (mesh of nodes): `Publish`/`Received`/`On<T>` across nodes |
| **SetNet.Redis** | Redis backplane: shared `ISessionStore`/`IBanStore`/`IRoomStore` across nodes (deps `SetNet.Auth`+`SetNet.Rooms`+`SetNet.BanList`+StackExchange.Redis) |
| **SetNet.Sharding** | consistent-hash `ShardRing` (virtual nodes) + a shard directory every node answers: clients ask any node which node owns a key, then connect there |
| **SetNet.LoadBalancer** | least-loaded node selection: an entry node keeps a registry of nodes with reported load/capacity and directs clients to the emptiest one |

**Logging** (`src/logging/`)
| Package | What it adds |
|---|---|
| **SetNet.Logging.Serilog** | `SerilogLogger : ILogger` routing diagnostics into Serilog |
| **SetNet.Logging.NLog** | `NLogLogger : ILogger` routing diagnostics into NLog |
| **SetNet.Logging.ZLogger** | `ZLoggerLogger : ILogger` routing diagnostics into ZLogger (zero-alloc, via `Microsoft.Extensions.Logging`) |

**Engine bindings** (`src/engine/`)
| Package | What it adds |
|---|---|
| **SetNet.Unity** | `MainThreadDispatcher` for Unity's main thread |
| **SetNet.StateSync.Unity** | Unity components: NetworkObject/Transform/Animator/Rigidbody/Behaviour + NetworkManager (**UPM source**, not NuGet) |
| **SetNet.Godot** | Godot 4 (C#) main-thread dispatcher + math conversions (deps `SetNet.StateSync` + `GodotSharp`) |
| **SetNet.StateSync.Godot** | Godot 4 replication components: NetworkObject/Transform/AnimationPlayer/RigidBody/Behaviour + NetworkManager (deps `SetNet.StateSync` + `SetNet.Godot` + `GodotSharp`) |

### Core extension points already in place (for composition packages)
- `SetNetSerializer.Use/Serialize/Deserialize` — pluggable serialization.
- Auto-discovered typed **and** `byte[]` handlers (`IServer/ClientMessageHandler<T>`) via `[MessageHandler]`.
- **Unified messaging protocol** (`SetNet.Protocol`, in core): `client.RequestAsync/PostAsync/On<T>` + server `[ProtocolChannel] IChannelService` + `peer.PublishRawAsync` — one envelope wire id, per-module `Channels`, shared correlation + subscription registries. The composition layer most game modules build on.
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
- **SetNet.NPC** — a unified abstraction for non-living interactive entities (vendors, bankers, quest-givers, buffers, teleporters): one `INpcBehaviour` per NPC type + spawn/interest/interact, delegating to the economy/quest/status modules via a capability hand-off. Detailed design: [design/SetNet.NPC.md](design/SetNet.NPC.md).
- **SetNet.Mobs** — hostile AI entities with **per-mob AI** (`IMobBrain`): ready-made aggressive / retaliate-only / ranged-kiter / caster brains + composable behaviour components, server-authoritative tick loop, threat, telegraphed abilities, StateSync replication, loot/XP/respawn on death. Detailed design: [design/SetNet.Mobs.md](design/SetNet.Mobs.md).
