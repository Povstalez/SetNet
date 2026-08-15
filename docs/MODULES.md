# SetNet modules — implemented & planned

> Maturity matters: the core and several companion packages are intended as stable library surface, while many
> gameplay/domain packages are reference implementations that you should adapt to your product rules. See
> [MODULE_STATUS.md](MODULE_STATUS.md) before choosing packages for production.

SetNet is a small core plus optional **companion packages** added by composition. Each depends only on `SetNet`
(unless noted) and is wired by an `xxxRuntime.Enable()` call + `server.UseXxx()` / `client.UseXxx()` extensions,
so the core stays lean and users pull only what they need. Simple apps can keep using the default runtime;
plugin hosts and multi-stack tests can use an explicit `SetNetRuntime` on `Configuration.Runtime`.

**Unified messaging (`SetNet.Protocol`, in core).** Every command/reply/event companion module below speaks the
same three verbs over one shared envelope wire id (`ProtocolTypes.Envelope`), demultiplexed by a per-module
`Channels.X` id + an `op`: correlated **request/reply** (`client.RequestAsync`/`RequestRawAsync`), **fire-and-forget**
(`client.PostAsync`/`PostRawAsync`), and **server push** (`client.On<T>`/`OnRaw` ↔ `peer.PublishRawAsync`), with the
server side handled by an auto-discovered `[ProtocolChannel(Channels.X)] IChannelService`. This means no per-module
wire-type reservations and one shared correlation/subscription mechanism instead of each module hand-rolling its own.
`SetNet.Rpc` rides it too — `client.CallAsync(methodId, req)` is a typed alias of `RequestAsync` on the `Channels.Rpc`
channel. A handful of modules with a genuinely different message shape stay off it and keep their own reserved ids:
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
| **SetNet.WebSockets** | WebSocket transport via `config.UseWebSockets()` (server via HttpListener, client via ClientWebSocket) |
| **SetNet.WebSockets.Unity** | WebSocket **client** transport for Unity **including WebGL** (browser) via a `.jslib` bridge; `config.UseUnityWebSockets(secure)` — ClientWebSocket off WebGL, browser WebSocket on WebGL; one `SetNet.WebSockets` server serves both (**UPM source**, not NuGet) |
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
| **SetNet.Ping** | round-trip **latency** measurement: `server.UsePing().Of(peer)` reads any player's RTT (ms) at any moment; `client.UsePing()` echoes + measures its own ping. Rides `Channels.Ping`, monotonic Stopwatch timing (deps `SetNet`) |
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

**World / AI** (`src/realtime/`) — server-side spatial libraries (GeoData/PathFinding have no wire protocol) + entity modules
| Package | What it adds |
|---|---|
| **SetNet.GeoData** | server-side world geometry behind one `IGeoData`: walkability, line-of-sight, can-walk-straight, ground height, raycast — over a 2.5D nav-grid (`GridGeoData`), an **L2-style multi-storey grid** (`LayeredGridGeoData` — stacked height layers per cell → floors/bridges without a nav-mesh), or a Y-aware nav-mesh (`NavMeshGeoData`); **`SectoredGeoData`** stitches many per-sector geodatas into one seamless world (+ `GeoDataManifest` `.geomap`); bake portable `GeoDataFile`s (no wire protocol) |
| **SetNet.PathFinding** | A* over `GridGeoData` (octile, no corner-cut), `LayeredGridGeoData` (per-layer, climbs stairs between floors), `NavMeshGeoData` (triangle A* → portals → string-pull), or `SectoredGeoData` (delegate within a sector + stitch across borders) + `PathFollower`; **pooled + generation-stamped search** (allocation-free hot path, built-once-reused for MMO scale) (deps `SetNet.GeoData`) |
| **SetNet.NPC** | non-living interactive entities (vendors/buffers/teleporters/dialogue): one `INpcBehaviour` per type + spawn/zone-interest/interact, delegating to the economy/status modules via a capability hand-off (deps `SetNet.GeoData` for `Vec3`) |
| **SetNet.Mobs** | hostile AI entities with **per-mob AI** (`IMobBrain`: ready aggressive / retaliate-only / ranged-kiter / caster brains + a composer), server-authoritative tick loop, perception, threat, telegraphed abilities, loot/respawn on death. **StateSync-optional** — replicates via an `IMobReplication` seam (drive it with `Update(dtMs)` and no StateSync at all, or plug the adapter below) (deps `SetNet.GeoData` + `SetNet.PathFinding`) |
| **SetNet.Mobs.StateSync** | optional adapter that replicates mobs over StateSync (`IMobReplication` → `ServerReplication`) (deps `SetNet.Mobs` + `SetNet.StateSync`) |
| **SetNet.Locomotion** | one **unified server-side movement tick**: create a `Mover` (auto-subscribed) and it advances along a pathfound route N Hz; replicates **nothing** — a `Started` hook lets you send the destination (L2-style, clients re-path) or `Mover.Waypoints` / `WaypointIndex` to send the route you already computed instead (deps `SetNet.GeoData` + `SetNet.PathFinding`) |
| **SetNet.Mobs.Locomotion** | opt-in bridge (`loco.AsMobMover()` → `MobOptions.Mover`) that advances mob positions through the shared `SetNet.Locomotion` tick instead of the built-in follower — players + mobs in one system (deps `SetNet.Mobs` + `SetNet.Locomotion`) |
| **SetNet.StateMachine** | tiny generic finite-state machine (states / guards / any-state transitions) for AI/gameplay — no wire protocol |
| **SetNet.BehaviorTree** | generic behavior tree (Sequence/Selector/Parallel + Inverter/Repeat/Cooldown decorators + Action/Condition/Wait leaves, fluent builder) — no wire protocol |
| **SetNet.Spawning** | zone-based mob spawning over `SetNet.Mobs`: box/circle areas + per-type counts + respawn delay, walkable-snapped via GeoData (deps `SetNet.Mobs` + `SetNet.GeoData`) |
| **SetNet.Accounts** | server-side **account store + auth**: generic `AccountServer<TAccount>` over any `IDocumentStore`; register/authenticate (pluggable PBKDF2 hasher), ban/unban, change password; subclass `AccountBase` for custom fields (deps `SetNet` + `SetNet.Persistence`) |
| **SetNet.CharacterStore** | server-side **character store**: generic `CharacterServer<TChar>` — create/list-by-account/rename, per-account slot limit, unique names, soft-delete + restore; subclass `CharacterBase` for custom fields (e.g. `VipUntil`) (deps `SetNet` + `SetNet.Persistence`) |
| **SetNet.GameData** | static **game-data tables** from JSON (item/skill/npc/spawn) → id-keyed `DataTable<TId,TRow>` lookups, hot-reloadable; rows are your POCOs with any custom columns (deps `System.Text.Json`) |
| **SetNet.Hitscan** | server-authoritative **instant-hit shooting** with a **pluggable `IHitDetector`** (bring your own collision) + shipped detectors: sphere targets (`ITargetProvider`), `IGeoData` world raycast, and a `CompositeHitDetector` (closest wins). `HitscanResolver.Fire(...)` → `HitResult` (deps `SetNet.GeoData`) |
| **SetNet.Projectiles** | server-authoritative **travelling projectiles**: spawn with velocity, advance each tick, **sweep-test** the path via the **same `IHitDetector`** as Hitscan; gravity, lifetime/range, `Hit`/`Expired`; auto-ticks via `SetNet.Ticks` (deps `SetNet.Hitscan` + `SetNet.GeoData` + `SetNet.Ticks`) |
| **SetNet.Inventory.Grid** | spatial **"tetris" inventory** (Diablo/Tarkov): width×height footprints, 90° rotation, occupancy checks, auto-fit add, move/rotate/remove, optional all-or-nothing stacking; pure data, zero deps |
| **SetNet.BoardGame** | **turn-based board/card game framework**: card primitives (deck/shuffle) + server-authoritative `ITurnGame<TState,TMove,TView>` with **per-player hidden-information views** + headless `TurnGameHost`; ships a complete deterministic **Durak (Подкидной дурак)** engine (deps `SetNet`) |
| **SetNet.LoginServer** | **L2-style login coordinator**: `server.UseLoginServer(...)` + `client.UseLogin()` → authenticate ▸ server list ▸ **one-time session token** handed to the chosen game server (validated against a shared `ILoginTokenStore`); composition over Accounts + LoadBalancer (deps `SetNet`) |
| **SetNet.Ticks** | one **central update loop**: register anything (`ITickable`/lambda) into a named channel with its own rate (Hz) + priority; drives them all from one place (30 Hz movement, 10 Hz AI, 1 Hz housekeeping) with per-channel fixed timestep; `Start()` internal timer **or** `Pump(dt)` from your loop. **Auto-subscription**: `new TickScheduler().MakeCurrent()` before your `UseXxx(...)` and `MobServer`/`LocomotionSystem`/`SpawningServer` subscribe themselves (no manual wiring); behaviour trees/state machines plug in via `Bind(ctx)`. **Zero deps** — the tick foundation lives here (not core), and the game-loop modules depend on it — no wire protocol |
| **SetNet.Notifications** | server→client notifications / toasts with an offline queue flushed on reconnect |
| **SetNet.Dialogue** | server-authoritative branching dialogue trees (guarded choices + side-effects), `StartAsync`/`ChooseAsync` |

**Character / RPG systems** (`src/realtime/`) — the combat foundation; Stats underpins the rest
| Package | What it adds |
|---|---|
| **SetNet.Stats** | your own stat schema + a `StatSet` whose values compute from a base plus flat / additive-% / multiplicative-% modifiers (clamped, cached, remove-by-source) — no wire protocol |
| **SetNet.Combat** | damage resolution between two `StatSet`s: pluggable `ICombatFormula`, configurable `CombatStatKeys`, crit + armor mitigation, a `Health` pool (deps `SetNet.Stats`) |
| **SetNet.Abilities** | abilities/skills with cooldown, resource cost, range and composable effects (damage / heal / timed buff) over stats/health/position seams — players and mobs alike (deps `SetNet.Stats` + `SetNet.Combat`) |
| **SetNet.Equipment** | custom equip slots (with accept rules); equipped items apply stat modifiers to the wearer's `StatSet`, moved in/out of Inventory (deps `SetNet.Stats` + `SetNet.Inventory`) |

**Infra** (`src/infra/`)
| Package | What it adds |
|---|---|
| **SetNet.DependencyInjection** | construct **every discovered component** (message handlers, `[ProtocolChannel]` channel services + `[Op]` classes, client `[Event]` handlers, `[RpcMethod]` RPC handlers) via an `IServiceProvider` with constructor injection — `provider.UseSetNet()` (`Microsoft.Extensions.DependencyInjection`) |
| **SetNet.Services** | tiny **service locator** so you don't hand-store every `UseXxx()` instance: `hub.Add(server.UseInventory())` once, then `Service.Get<InventoryServer>()` anywhere — ambient (`MakeCurrent`) or per-server (`server.Services()`). Lets brains/handlers/helpers reach `loco`/`ticks`/`dialogue`/… without constructor plumbing (deps `SetNet`) |
| **SetNet.Hosting** | run a server as an `IHostedService` (`Microsoft.Extensions.Hosting`) |
| **SetNet.HealthChecks** | `IHealthCheck` for liveness + connections (`Microsoft.Extensions.Diagnostics.HealthChecks`) |
| **SetNet.Inspector** | HttpListener dashboard (`/metrics` JSON + HTML) |
| **SetNet.Gateway** | raw-relay reverse proxy: accept clients → forward to per-client backend |
| **SetNet.Cluster** | server-to-server broadcast bus (mesh of nodes): `Publish`/`Received`/`On<T>` across nodes |
| **SetNet.Redis** | Redis backplane: shared `ISessionStore`/`IBanStore`/`IRoomStore` across nodes (deps `SetNet.Auth`+`SetNet.Rooms`+`SetNet.BanList`+StackExchange.Redis) |
| **SetNet.Sharding** | consistent-hash `ShardRing` (virtual nodes) + a shard directory every node answers: clients ask any node which node owns a key, then connect there |
| **SetNet.LoadBalancer** | least-loaded node selection: an entry node keeps a registry of nodes with reported load/capacity and directs clients to the emptiest one |
| **SetNet.Persistence** | durable-state seam for module stores: `IDocumentStore<T>` (get/set/enumerate) + `ISnapshotStore` (byte blobs), with in-memory and JSON-file implementations (deps `System.Text.Json`) |
| **SetNet.Persistence.Dapper** | `IDocumentStore<T>`/`ISnapshotStore` on **any ADO.NET DB** via Dapper — value stored as JSON in a key/value table; custom fields need no schema change; portable upsert (deps `Dapper`) |
| **SetNet.Persistence.Postgres** | PostgreSQL store via Npgsql — value in a **JSONB** column, so custom fields stay **SQL-queryable/indexable** (`doc->>'VipUntil'`); native `ON CONFLICT` upsert (deps `Npgsql`) |
| **SetNet.Persistence.EfCore** | **provider-agnostic** EF Core store — pass `DbContextOptions` (SQLite/Postgres/SQL Server); JSON key/value + snapshot tables (deps `Microsoft.EntityFrameworkCore.Relational`) |
| **SetNet.Persistence.MongoDB** | MongoDB store via MongoDB.Driver — value as **native BSON** (queryable/indexable); `ReplaceOne` upsert (deps `MongoDB.Driver`) |
| **SetNet.Docs** | reflects `[MessageHandler]` / `[ProtocolChannel]`+`[Op]`/`[Event]` / `[RpcMethod]` into a Markdown protocol report (`ProtocolDocs.Generate()`) |

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
| **SetNet.GeoData.Unity** | Editor tool to bake a `SetNet.GeoData` file from a Unity NavMesh, colliders (flat grid), a **multi-surface sweep** (multi-storey layered grid), or **tiled sectors** (+ `.geomap` manifest); one-click auto-bounds + a Scene-view **debug visualizer** (`GeoDataGizmo`) (**UPM source**, not NuGet) |
| **SetNet.Locomotion.Unity** | client-side **`NavAgent`** component: walks a GameObject along a path (that the client computed from the destination point the server sent) at a `Speed` you set (your replicated move-speed) (**UPM source**, not NuGet) |
| **SetNet.Godot** | Godot 4 (C#) main-thread dispatcher + math conversions (deps `SetNet.StateSync` + `GodotSharp`) |
| **SetNet.StateSync.Godot** | Godot 4 replication components: NetworkObject/Transform/AnimationPlayer/RigidBody/Behaviour + NetworkManager (deps `SetNet.StateSync` + `SetNet.Godot` + `GodotSharp`) |

### Core extension points already in place (for composition packages)
- `SetNetRuntime` — scoped serializer, handler registry, protocol subscriptions, and long-lived module lifetime. `SetNetRuntime.Default` preserves the historical process-wide behavior.
- `SetNetSerializer.Use/Serialize/Deserialize` — compatibility façade over `SetNetRuntime.Default`; scoped endpoints should prefer `Configuration.Runtime`.
- Auto-discovered or explicit typed **and** `byte[]` handlers (`IServer/ClientMessageHandler<T>`) via `[MessageHandler]` and `SetNetRuntime.Handlers`.
- **Unified messaging protocol** (`SetNet.Protocol`, in core): `client.RequestAsync/PostAsync/On<T>` + server `[ProtocolChannel] IChannelService` + `peer.PublishRawAsync` — one envelope wire id, per-module `Channels`, shared correlation + runtime-scoped subscription registries. The composition layer most game modules build on.
- `BaseClient/BasePeer.SendAsync<T>` and `SendRawAsync` — **public**.
- `BaseSocket.OnRawFrame` (intercept/consume) + `SendRawAsync` — relay/proxy primitive.
- `BaseServer.InboundAuthorizer` + `BaseSocket.AllowInbound` — per-frame inbound gate.
- `BaseClient.Connected` event (after connect+reconnect), `BaseServer.PeerDisconnected` + `BaseServer.PeerConnected` events (once per peer).
- `BaseServer.RegisterModule` / `BaseClient.RegisterModule` — tie module cleanup to endpoint lifetime; `SetNetRuntime.RegisterModule` covers process/runtime-level resources.
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
