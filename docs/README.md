<p align="center">
  <img src="../assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet documentation

Everything about SetNet in one place. Start here, then dive into a specific guide or package.

## Start here

- **📖 [Full documentation site — setnet.lemeshev.dev](https://setnet.lemeshev.dev)** — the browsable docs site: the guides, every module's README, and a **complete generated API reference** (every public type, property, method and option, from the source XML docs). Built with DocFX. Build it locally with `bash docfx/gen.sh` (needs `dotnet tool install -g docfx`) → opens `_site/`.
- **[Main README](../README.md)** — what SetNet is, quick start, transport selection, lifecycle, hardening.
- **[User guide (GUIDE.en.md)](GUIDE.en.md)** — the full manual: handlers, protocol channels (`[ProtocolChannel]`/`[Op]`/`[Event]`) and RPC, transports, reliable channels, reconnect, batching, the complete `Configuration` reference, production checklist. *(Українською: [GUIDE.ua.md](GUIDE.ua.md))*
- **[Performance (PERFORMANCE.en.md)](PERFORMANCE.en.md)** — throughput model, scaling limits, roadmap. *(Українською: [PERFORMANCE.ua.md](PERFORMANCE.ua.md))*
- **[Module catalog (MODULES.md)](MODULES.md)** — every companion package, grouped, with links + the core extension points.
- **[Communication model (COMMUNICATION.md)](COMMUNICATION.md)** — all client/server send & receive methods: core `SendAsync`, the unified `SetNet.Protocol` (request/reply, fire-and-forget, push; `[Op]`/`[Event]`), and modules (RPC, StateSync, Streams, Voice, …), with a full worked example.

## The mental model

SetNet is a **small core** (`SetNet`) plus **optional companion packages** you pull in only when you need them. Every companion is added by **composition** — no base classes to inherit — following the same three-part pattern:

1. Configure serialization either on the default runtime (`SetNetSerializer.Use(...)`) or on an explicit `SetNetRuntime` assigned to `Configuration.Runtime`. Call `XxxRuntime.Enable()` when a companion package requires its assembly to be loaded for discovery.
2. `server.UseXxx(...)` and/or `client.UseXxx(...)` to attach the feature. Endpoint-scoped modules are disposed with the server/client when they register through the core module lifecycle.
3. Use the returned driver / events. Everything runs alongside your normal `[MessageHandler]` messages on the same connection — packages never conflict (the command/reply/event packages share the one **`SetNet.Protocol`** envelope, demultiplexed by a per-module `Channels` id; a few shape-different packages keep their own reserved ids — see [MODULES.md](MODULES.md)).

## Which package do I need?

| I want to… | Package(s) |
|---|---|
| Pick a serialization format | [MessagePack](../src/serializers/SetNet.MessagePack/README.md) (default), [Json](../src/serializers/SetNet.Json/README.md), [MemoryPack](../src/serializers/SetNet.MemoryPack/README.md) (AOT/Unity), [Protobuf](../src/serializers/SetNet.Protobuf/README.md) (cross-language), [Compression](../src/serializers/SetNet.Compression/README.md) (Brotli decorator) |
| Run over the web / behind proxies | [WebSockets](../src/transports/SetNet.WebSockets/README.md) |
| Fast, deterministic tests / co-host in one process | [InMemory](../src/transports/SetNet.InMemory/README.md) |
| Request/response with a return value | [Rpc](../src/messaging/SetNet.Rpc/README.md) |
| Log in players / sessions | [Auth](../src/security/SetNet.Auth/README.md) (+ [Auth.Jwt](../src/security/SetNet.Auth.Jwt/README.md) / [Auth.OAuth](../src/security/SetNet.Auth.OAuth/README.md)) |
| Ban abusers / stop connection floods | [BanList](../src/security/SetNet.BanList/README.md), [DdosGuard](../src/security/SetNet.DdosGuard/README.md), [RateLimit](../src/net/SetNet.RateLimit/README.md) |
| Send messages bigger than a UDP datagram | [Fragmentation](../src/net/SetNet.Fragmentation/README.md) |
| Transfer files / patches / replays (progress, resume) | [Streams](../src/messaging/SetNet.Streams/README.md) |
| Prioritise / rate-shape outbound traffic | [Priority](../src/net/SetNet.Priority/README.md) + [Congestion](../src/net/SetNet.Congestion/README.md) |
| Keep input/chat snappy next to bulk traffic | [Multiplex](../src/net/SetNet.Multiplex/README.md) |
| Connect two players directly (P2P) | [NatPunch](../src/net/SetNet.NatPunch/README.md), fallback [Relay](../src/net/SetNet.Relay/README.md) |
| Open the player's router port automatically | [Upnp](../src/net/SetNet.Upnp/README.md) |
| Lobbies / rooms | [Rooms](../src/realtime/SetNet.Rooms/README.md) (+ [HostMigration](../src/realtime/SetNet.Rooms.HostMigration/README.md)) |
| Matchmaking | [Matchmaking](../src/realtime/SetNet.Matchmaking/README.md) |
| Friends queue together | [Party](../src/realtime/SetNet.Party/README.md) |
| Text chat | [Chat](../src/realtime/SetNet.Chat/README.md) |
| Replicate a moving world (positions, NPCs) | [StateSync](../src/realtime/SetNet.StateSync/README.md) (+ [SpatialGrid](../src/realtime/SetNet.StateSync.SpatialGrid/README.md), [LagCompensation](../src/realtime/SetNet.StateSync.LagCompensation/README.md), [Prediction](../src/realtime/SetNet.StateSync.Prediction/README.md), [NetworkVariable](../src/realtime/SetNet.StateSync.NetworkVariable/README.md), [Rpc](../src/realtime/SetNet.StateSync.Rpc/README.md)) |
| Deterministic RTS-style networking | [Lockstep](../src/realtime/SetNet.Lockstep/README.md) |
| Player inventories / items | [Inventory](../src/realtime/SetNet.Inventory/README.md) |
| Player-to-player trading | [Trade](../src/realtime/SetNet.Trade/README.md) |
| In-game mail with attachments | [Mail](../src/realtime/SetNet.Mail/README.md) |
| Seamless world across many nodes | [Zones](../src/realtime/SetNet.Zones/README.md) (+ [Sharding](../src/infra/SetNet.Sharding/README.md)) |
| Player currencies / money | [Wallet](../src/realtime/SetNet.Wallet/README.md) |
| NPC shops (buy/sell) | [Vendor](../src/realtime/SetNet.Vendor/README.md) |
| Player-driven marketplace | [Auction](../src/realtime/SetNet.Auction/README.md) |
| Crafting recipes | [Crafting](../src/realtime/SetNet.Crafting/README.md) |
| Loot / drop tables | [Loot](../src/realtime/SetNet.Loot/README.md) |
| Quests & objectives | [Quests](../src/realtime/SetNet.Quests/README.md) |
| Levels & XP | [Progression](../src/realtime/SetNet.Progression/README.md) |
| Guilds / clans (+ bank) | [Guilds](../src/realtime/SetNet.Guilds/README.md) |
| Commodity marketplace (order book) | [Marketplace](../src/realtime/SetNet.Marketplace/README.md) |
| Buffs / debuffs (combat) | [StatusEffects](../src/realtime/SetNet.StatusEffects/README.md) |
| Server-side world geometry (walkable, line-of-sight, floors/multi-storey, sectors) | [GeoData](../src/realtime/SetNet.GeoData/README.md) |
| Find paths & move entities along them | [PathFinding](../src/realtime/SetNet.PathFinding/README.md) |
| Interactive NPCs (vendors, teleporters, dialogue) | [NPC](../src/realtime/SetNet.NPC/README.md) |
| Hostile AI monsters (chase / attack / per-mob AI) | [Mobs](../src/realtime/SetNet.Mobs/README.md) (+ [Mobs.StateSync](../src/realtime/SetNet.Mobs.StateSync/README.md)) |
| Move everything server-side through one tick (players/mobs/…) | [Locomotion](../src/realtime/SetNet.Locomotion/README.md) (+ [Locomotion.Unity](../src/engine/SetNet.Locomotion.Unity/README.md) NavAgent) |
| Custom character stats (attack power, defense, speed…) | [Stats](../src/realtime/SetNet.Stats/README.md) |
| Damage / combat resolution | [Combat](../src/realtime/SetNet.Combat/README.md) |
| Abilities / skills (cooldowns, costs, effects) | [Abilities](../src/realtime/SetNet.Abilities/README.md) |
| Equipment / gear that changes stats | [Equipment](../src/realtime/SetNet.Equipment/README.md) |
| Reusable AI (state machine / behavior tree) | [StateMachine](../src/realtime/SetNet.StateMachine/README.md), [BehaviorTree](../src/realtime/SetNet.BehaviorTree/README.md) |
| Keep zones populated with mobs (spawn/respawn) | [Spawning](../src/realtime/SetNet.Spawning/README.md) |
| Toasts / notifications to players | [Notifications](../src/realtime/SetNet.Notifications/README.md) |
| Branching NPC dialogue | [Dialogue](../src/realtime/SetNet.Dialogue/README.md) |
| Persist module state (save-games, snapshots) | [Persistence](../src/infra/SetNet.Persistence/README.md) |
| Auto-document your wire protocol | [Docs](../src/infra/SetNet.Docs/README.md) |
| Direct players to the emptiest node | [LoadBalancer](../src/infra/SetNet.LoadBalancer/README.md) |
| Host in ASP.NET Core / use DI / probe health | [Hosting](../src/infra/SetNet.Hosting/README.md), [DependencyInjection](../src/infra/SetNet.DependencyInjection/README.md), [HealthChecks](../src/infra/SetNet.HealthChecks/README.md) |
| Watch live metrics / shard players | [Inspector](../src/infra/SetNet.Inspector/README.md), [Gateway](../src/infra/SetNet.Gateway/README.md) |
| Route keys/rooms across many nodes | [Sharding](../src/infra/SetNet.Sharding/README.md) (+ [Redis](../src/infra/SetNet.Redis/README.md), [Cluster](../src/infra/SetNet.Cluster/README.md)) |
| Log to Serilog | [Logging.Serilog](../src/logging/SetNet.Logging.Serilog/README.md) |
| Use it in Unity / Godot | [Unity](../src/engine/SetNet.Unity/README.md) + [StateSync.Unity](../src/engine/SetNet.StateSync.Unity/README.md), [Godot](../src/engine/SetNet.Godot/README.md) |
| Bake world geometry from a Unity scene | [GeoData.Unity](../src/engine/SetNet.GeoData.Unity/README.md) (NavMesh / colliders / multi-storey / sectors + Scene-view visualizer) |

## Serialization: typed payloads vs raw envelopes

A recurring question: *why do some APIs hand me `byte[]`?* There are two different payloads at play, and they're treated differently on purpose:

- **The inter-package envelope** (an RPC correlation id, a room code, a lockstep turn number, an opcode) is **hand-framed as `byte[]`** by each companion package — deliberately, so the package has **no dependency on any serializer**. That's what makes RPC / Rooms / Auth / Matchmaking / StateSync serializer-agnostic (MessagePack-free). You never see this layer.

- **Your game payload** inside the envelope goes through the endpoint's **`SetNetRuntime` serializer** wherever the type is known, so you work with **typed objects**, not bytes. `SetNetSerializer` is the compatibility façade over `SetNetRuntime.Default`:
  - `client.CallAsync<TReq, TResp>(...)` (Rpc) — typed both ways.
  - `rooms.BroadcastAsync<T>(msg)` — typed send.
  - `client.UseLockstep<TInput>()` — `SubmitInput(TInput)` + `TurnReady` delivers `IReadOnlyDictionary<string, TInput>`.
  - `stateRpc.SendAsync<T>(netId, methodId, arg)` and `peer.SendFragmentedAsync<T>(type, msg, ...)` — typed send.

- **On receive**, where one type is known you get it typed. Where a channel multiplexes **many types under an id** (e.g. StateSync.Rpc, whose `methodId` selects a different argument type each time), a single generic *event* is impossible — so those packages expose a **typed handler registration** instead:
  - `stateRpc.On<T>(methodId, (…, arg) => …)` and `rooms.On<T>(messageType, (from, msg) => …)` — register a typed handler per id; the payload is deserialized to `T` for you. This is the recommended path (pair with `rooms.BroadcastAsync<T>(messageType, msg)` / `stateRpc.SendAsync<T>(…)`).
  - A raw **`Received`** / **`MessageReceived`** event remains as a catch-all for ids you didn't register (relays, logging, dynamic dispatch); there the `byte[]` **is** serializer output, so decode it with `SetNetSerializer.Deserialize<T>(payload)` on the default runtime or `runtime.Deserialize<T>(payload)` in scoped code.

Rule of thumb: **register a typed `On<T>` handler / use the typed `<T>` overload** when one exists; only drop to the raw `byte[]` event for advanced/relay scenarios.

## Reserved wire types

Companion packages claim ids from the top of the `ushort` range so they never collide with your application message types (number yours from `0` upward). Most game/command packages now share a **single** envelope id — `SetNet.Protocol`'s `ProtocolTypes.Envelope` (**65447**) — and are demultiplexed by a per-module `Channels` id + `op`, so there are no longer per-package wire-type triples to reserve. `SetNet.Rpc` rides the shared envelope too (`CallAsync` is a typed alias of `RequestAsync`). The remaining reserved ids belong to the shape-different packages (`SetNet.StateSync`(+`.Rpc`), `SetNet.Voice`, `SetNet.Fragmentation`, `SetNet.Multiplex`, `SetNet.Streams`, `SetNet.Cluster`, `SetNet.Auth`, `SetNet.ProofOfWork`) plus the core system types (65533–65535) — the full map is in [MODULES.md](MODULES.md).

## Installing from GitHub Packages

Releases are published to **NuGet.org** (public, no auth) *and* to this repo's **GitHub Packages** feed. To consume the GitHub Packages feed, copy [`docs/nuget.config.example`](nuget.config.example) to your solution root as `nuget.config` and set a token:

```bash
export GITHUB_PACKAGES_PAT=<a token with read:packages>
dotnet add package SetNet
dotnet add package SetNet.Rpc
```

Notes: GitHub Packages requires a token with `read:packages` **even for public packages**; keep it in an env var, never commit it. Publishing is automated — a published GitHub Release triggers [`.github/workflows/publish-github-packages.yml`](../.github/workflows/publish-github-packages.yml) (and the NuGet.org workflow), packing every library under `src/` at the release-tag version and pushing with `--skip-duplicate`. You can also run the workflow manually (`workflow_dispatch`) with an explicit version.

## Building & testing

```bash
dotnet build                                            # whole solution
dotnet test tests/SetNet.UnitTests/SetNet.UnitTests.csproj
dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock|bench>
```

Packages live under `src/<category>/`; the [InMemory transport](../src/transports/SetNet.InMemory/README.md) makes integration tests socket-free and deterministic.

## License

MIT — see [LICENSE](../LICENSE).
