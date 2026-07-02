<p align="center">
  <img src="../assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet documentation

Everything about SetNet in one place. Start here, then dive into a specific guide or package.

## Start here

- **[Main README](../README.md)** — what SetNet is, quick start, transport selection, lifecycle, hardening.
- **[User guide (GUIDE.en.md)](GUIDE.en.md)** — the full manual: handlers, transports, reliable channels, reconnect, batching, the complete `Configuration` reference, production checklist. *(Українською: [GUIDE.ua.md](GUIDE.ua.md))*
- **[Performance (PERFORMANCE.en.md)](PERFORMANCE.en.md)** — throughput model, scaling limits, roadmap. *(Українською: [PERFORMANCE.ua.md](PERFORMANCE.ua.md))*
- **[Module catalog (MODULES.md)](MODULES.md)** — every companion package, grouped, with links + the core extension points.

## The mental model

SetNet is a **small core** (`SetNet`) plus **optional companion packages** you pull in only when you need them. Every companion is added by **composition** — no base classes to inherit — following the same three-part pattern:

1. `XxxRuntime.Enable()` once at startup (so the package's auto-discovered handlers are found), and register a serializer (`SetNetSerializer.Use(...)`).
2. `server.UseXxx(...)` and/or `client.UseXxx(...)` to attach the feature.
3. Use the returned driver / events. Everything runs alongside your normal `[MessageHandler]` messages on the same connection — packages never conflict (each owns a reserved wire-type range; see [MODULES.md](MODULES.md)).

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
| Host in ASP.NET Core / use DI / probe health | [Hosting](../src/infra/SetNet.Hosting/README.md), [DependencyInjection](../src/infra/SetNet.DependencyInjection/README.md), [HealthChecks](../src/infra/SetNet.HealthChecks/README.md) |
| Watch live metrics / shard players | [Inspector](../src/infra/SetNet.Inspector/README.md), [Gateway](../src/infra/SetNet.Gateway/README.md) |
| Route keys/rooms across many nodes | [Sharding](../src/infra/SetNet.Sharding/README.md) (+ [Redis](../src/infra/SetNet.Redis/README.md), [Cluster](../src/infra/SetNet.Cluster/README.md)) |
| Log to Serilog | [Logging.Serilog](../src/logging/SetNet.Logging.Serilog/README.md) |
| Use it in Unity / Godot | [Unity](../src/engine/SetNet.Unity/README.md) + [StateSync.Unity](../src/engine/SetNet.StateSync.Unity/README.md), [Godot](../src/engine/SetNet.Godot/README.md) |

## Serialization: typed payloads vs raw envelopes

A recurring question: *why do some APIs hand me `byte[]`?* There are two different payloads at play, and they're treated differently on purpose:

- **The inter-package envelope** (an RPC correlation id, a room code, a lockstep turn number, an opcode) is **hand-framed as `byte[]`** by each companion package — deliberately, so the package has **no dependency on any serializer**. That's what makes RPC / Rooms / Auth / Matchmaking / StateSync serializer-agnostic (MessagePack-free). You never see this layer.

- **Your game payload** inside the envelope goes through **`SetNetSerializer`** wherever the type is known, so you work with **typed objects**, not bytes:
  - `client.CallAsync<TReq, TResp>(...)` (Rpc) — typed both ways.
  - `rooms.BroadcastAsync<T>(msg)` — typed send.
  - `client.UseLockstep<TInput>()` — `SubmitInput(TInput)` + `TurnReady` delivers `IReadOnlyDictionary<string, TInput>`.
  - `stateRpc.SendAsync<T>(netId, methodId, arg)` and `peer.SendFragmentedAsync<T>(type, msg, ...)` — typed send.

- **On receive**, where one type is known you get it typed. Where a channel multiplexes **many types under an id** (e.g. StateSync.Rpc, whose `methodId` selects a different argument type each time), a single generic *event* is impossible — so those packages expose a **typed handler registration** instead:
  - `stateRpc.On<T>(methodId, (…, arg) => …)` and `rooms.On<T>(messageType, (from, msg) => …)` — register a typed handler per id; the payload is deserialized to `T` for you. This is the recommended path (pair with `rooms.BroadcastAsync<T>(messageType, msg)` / `stateRpc.SendAsync<T>(…)`).
  - A raw **`Received`** / **`MessageReceived`** event remains as a catch-all for ids you didn't register (relays, logging, dynamic dispatch); there the `byte[]` **is** the `SetNetSerializer` output, so you decode it with `SetNetSerializer.Deserialize<T>(payload)`.

Rule of thumb: **register a typed `On<T>` handler / use the typed `<T>` overload** when one exists; only drop to the raw `byte[]` event for advanced/relay scenarios.

## Reserved wire types

Companion packages claim ids from the top of the `ushort` range so they never collide with your application message types (number yours from `0` upward). The reserved block is currently the range **65498–65535** — the full map is in [MODULES.md](MODULES.md).

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
