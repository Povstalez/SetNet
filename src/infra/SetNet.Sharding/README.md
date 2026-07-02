<p align="center">
  <img src="https://raw.githubusercontent.com/Povstalez/SetNet/master/assets/icon.png" alt="SetNet" width="96">
</p>

# SetNet.Sharding

**Consistent-hash routing for [SetNet](https://www.nuget.org/packages/SetNet) clusters: every key has exactly one home node, and any node can tell you which.**

Rooms, relay sessions and matchmaking queues in SetNet are node-local — which is fine on one server, but on many you need every client interested in room `ABC123` to land on the *same* node. `SetNet.Sharding` provides that: a **consistent-hash ring** (virtual nodes, ~1/N key movement on membership change) mapping any string key to its owner node, plus a **directory service** every node answers, so a client can connect to any entry node, ask "where does key X live?", and dial the returned node. Added by **composition** — no base class.

## Install

```bash
dotnet add package SetNet
dotnet add package SetNet.Sharding
```

## Usage

Call `ShardingRuntime.Enable()` once at startup on both ends (before handler discovery).

**Every server node** — same node list everywhere:

```csharp
ShardingRuntime.Enable();
var nodes = new List<ShardNode>
{
    new ShardNode { NodeId = "eu-1", Host = "eu1.game.example", Port = 5000 },
    new ShardNode { NodeId = "eu-2", Host = "eu2.game.example", Port = 5000 },
    new ShardNode { NodeId = "us-1", Host = "us1.game.example", Port = 5000 },
};
var sharding = server.UseSharding(new ShardingOptions { Nodes = nodes, SelfNodeId = "eu-1" });

// server-side routing, e.g. inside a room-create handler:
if (!sharding.IsLocal(roomCode))
    /* redirect the client to sharding.Locate(roomCode) */ ;
```

**Client** — ask any node, then go where the key lives:

```csharp
ShardingRuntime.Enable();
var sharding = entryClient.UseSharding();

ShardNode home = await sharding.LocateAsync(roomCode);
var gameClient = new GameClient(new Configuration { Host = home.Host, Port = home.Port, /* ... */ });
await gameClient.ConnectAsync();          // rooms/relay/matchmaking for this code now work cluster-wide
```

**Membership changes** — swap the ring on every node (broadcast however you like, e.g. [`SetNet.Cluster`](https://www.nuget.org/packages/SetNet.Cluster)):

```csharp
cluster.On<NodesChanged>(msg => sharding.UpdateNodes(msg.Nodes));   // atomic swap, ~1/N keys remap
```

## API

| Member | Purpose |
|---|---|
| `server.UseSharding(ShardingOptions)` → `ShardingServer` | directory + in-process routing |
| `ShardingServer.Locate(key)` / `IsLocal(key)` / `UpdateNodes(nodes)` / `Ring` | server-side ring access |
| `client.UseSharding()` → `ShardingClient` | directory queries |
| `ShardingClient.LocateAsync(key)` / `ListNodesAsync()` | who owns a key / full node list |
| `ShardRing(nodes, virtualNodes = 128)` | the ring itself — `GetNode(key)`, `GetNodes(key, n)` (owner + replicas); usable standalone |
| `ShardingOptions` | `Nodes`, `SelfNodeId`, `VirtualNodes` |
| `ShardingRuntime.Enable()` | one-time bootstrap so the handlers are discovered |

## Notes

- **Reserved wire types 65490 / 65491.** Don't reuse them.
- **Deterministic everywhere.** The ring hashes with FNV-1a over `NodeId` — the same node list yields the same ring on every node and across restarts. Keep `NodeId`s stable; renaming one remaps its keys.
- **Directory, not proxy.** Clients are *told* where to go and connect there themselves (cheap, no extra hop). If you need transparent forwarding instead, put [`SetNet.Gateway`](https://www.nuget.org/packages/SetNet.Gateway) in front and use `ShardRing` inside its backend selector.
- **Consistency is yours.** The package doesn't gossip membership — push `UpdateNodes` to all nodes together (via `SetNet.Cluster`, config rollout, orchestrator). During a brief divergence two nodes may answer differently; design flows to re-query on connect failure.
- **Replicas.** `ShardRing.GetNodes(key, n)` returns the owner plus the next n−1 distinct nodes — the natural replica set for warm standbys or redundant placement.

## Documentation & source

- 🐙 https://github.com/Povstalez/SetNet — full module catalog in [docs/MODULES.md](https://github.com/Povstalez/SetNet/blob/master/docs/MODULES.md)

## License

MIT
